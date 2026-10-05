using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Swtor.Formats.Gr2;

namespace Swtor.App;

/// <summary>Draws one GR2 model in bind pose with an orbit camera.</summary>
public sealed class ModelPreview : IDisposable
{
    private sealed class GpuMesh(VertexBuffer vertices, IndexBuffer indices, Gr2Piece[] pieces)
    {
        public VertexBuffer Vertices { get; } = vertices;
        public IndexBuffer Indices { get; } = indices;
        public Gr2Piece[] Pieces { get; } = pieces;
        public Texture2D? Texture { get; set; }

        /// <summary>Textures for single material slots. They replace <see cref="Texture"/> for pieces with that material index.</summary>
        public IReadOnlyDictionary<int, Texture2D>? PieceTextures { get; init; }
    }

    private static readonly Color[] PieceColors =
    [
        new(200, 200, 205), new(230, 150, 90), new(110, 170, 230), new(140, 210, 140),
        new(220, 120, 170), new(230, 210, 100),
    ];

    private readonly GraphicsDevice _device;
    private readonly BasicEffect _surface;
    private readonly BasicEffect _lines;
    private readonly List<GpuMesh> _meshes = [];
    private readonly HashSet<Texture2D> _cutOut = [];
    private VertexPositionColor[] _grid = [];

    private Vector3 _boundsMin = new(float.MaxValue), _boundsMax = new(float.MinValue);
    private Vector3 _target;
    private float _yaw = 0.6f, _pitch = 0.3f, _distance = 1f, _radius = 1f;

    public bool Wireframe { get; set; }
    public bool CullBackfaces { get; set; }
    public bool ShowGrid { get; set; } = true;
    public bool HasModel => _meshes.Count > 0;

    /// <summary>Diffuse texture for all meshes. The caller owns it. Null draws a color per material.</summary>
    public Texture2D? Texture
    {
        get => _meshes.Count > 0 ? _meshes[0].Texture : null;
        set
        {
            foreach (var mesh in _meshes) mesh.Texture = value;
        }
    }

    public ModelPreview(GraphicsDevice device)
    {
        _device = device;
        _surface = new BasicEffect(device) { PreferPerPixelLighting = true };
        _surface.EnableDefaultLighting();
        // Default lighting adds a white specular highlight. The game textures are matte without a specular map, so remove it.
        _surface.SpecularColor = Vector3.Zero;
        _lines = new BasicEffect(device) { VertexColorEnabled = true, LightingEnabled = false };
    }

    /// <summary>Replaces the current content with one model. Must be called on the game thread (creates GPU buffers).</summary>
    public void Load(Gr2Model model)
    {
        Clear();
        Add(model, null);
        Frame();
    }

    /// <summary>
    /// Adds a model to the scene with its own texture (the caller owns the texture).
    /// Call <see cref="Frame"/> after the last model so the camera fits the scene.
    /// </summary>
    public void Add(Gr2Model model, Texture2D? texture, IReadOnlyDictionary<int, Texture2D>? pieceTextures = null)
    {
        var min = _boundsMin;
        var max = _boundsMax;

        foreach (var mesh in model.Meshes)
        {
            var normals = mesh.Normals ?? ComputeNormals(mesh);
            var uvs = mesh.UvSets.Count > 0 ? mesh.UvSets[0] : null;
            var vertices = new VertexPositionNormalTexture[mesh.VertexCount];
            for (int i = 0; i < vertices.Length; i++)
            {
                var p = new Vector3(mesh.Positions[i].X, mesh.Positions[i].Y, mesh.Positions[i].Z);
                var n = new Vector3(normals[i].X, normals[i].Y, normals[i].Z);
                var uv = uvs is null ? Vector2.Zero : new Vector2(uvs[i].X, uvs[i].Y);
                vertices[i] = new VertexPositionNormalTexture(p, n, uv);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            var vb = new VertexBuffer(_device, typeof(VertexPositionNormalTexture), vertices.Length, BufferUsage.WriteOnly);
            vb.SetData(vertices);
            var ib = new IndexBuffer(_device, IndexElementSize.SixteenBits, mesh.Indices.Length, BufferUsage.WriteOnly);
            ib.SetData(mesh.Indices);
            _meshes.Add(new GpuMesh(vb, ib, mesh.Pieces.ToArray()) { Texture = texture, PieceTextures = pieceTextures });
        }
        _boundsMin = min;
        _boundsMax = max;
    }

    /// <summary>
    /// Marks a texture whose alpha channel cuts holes (0 = hole, 255 = solid). Pieces with it are drawn after the others with
    /// alpha blending, so the parts behind them (for example the skin under a small top) are already drawn. Cleared by <see cref="Clear"/>.
    /// </summary>
    public void MarkCutOut(Texture2D texture) => _cutOut.Add(texture);

    private object? _frameOwner;

    /// <summary>
    /// Like <see cref="Frame()"/>, but keeps the camera when the same <paramref name="owner"/> framed the previous scene.
    /// Use it when a panel rebuilds its scene after each change, so the view does not jump.
    /// </summary>
    public void Frame(object owner)
    {
        if (_meshes.Count == 0) return;
        if (!ReferenceEquals(_frameOwner, owner))
        {
            Frame();
            _frameOwner = owner;
            return;
        }
        // Keep target, distance and angles. Only the grid follows the new bounds.
        BuildGrid(_boundsMin.Y);
    }

    /// <summary>Points the camera at the whole scene and moves the grid to its floor.</summary>
    public void Frame()
    {
        if (_meshes.Count == 0) return;
        _target = (_boundsMin + _boundsMax) / 2f;
        _radius = Math.Max(Vector3.Distance(_boundsMin, _boundsMax) / 2f, 0.01f);
        _distance = _radius * 2.6f;
        _frameOwner = null;
        BuildGrid(_boundsMin.Y);
    }

    /// <summary>Frees the GPU buffers of the current scene. Textures are not freed: the caller owns them.</summary>
    public void Clear()
    {
        foreach (var m in _meshes)
        {
            m.Vertices.Dispose();
            m.Indices.Dispose();
        }
        _meshes.Clear();
        _cutOut.Clear();
        _boundsMin = new Vector3(float.MaxValue);
        _boundsMax = new Vector3(float.MinValue);
    }

    public void Dispose()
    {
        Clear();
        _surface.Dispose();
        _lines.Dispose();
    }

    /// <summary>
    /// Updates the camera. Left drag orbits, right or middle drag pans, wheel zooms.
    /// </summary>
    public void HandleInput(MouseState previous, MouseState current)
    {
        var delta = current.Position - previous.Position;
        if (current.LeftButton == ButtonState.Pressed)
        {
            _yaw -= delta.X * 0.01f;
            _pitch = MathHelper.Clamp(_pitch + delta.Y * 0.01f, -1.5f, 1.5f);
        }
        if (current.RightButton == ButtonState.Pressed || current.MiddleButton == ButtonState.Pressed)
        {
            var view = Matrix.CreateLookAt(EyePosition(), _target, Vector3.Up);
            float scale = _distance * 0.0015f;
            _target += (-view.Right * delta.X + view.Up * delta.Y) * scale;
        }
        int wheel = current.ScrollWheelValue - previous.ScrollWheelValue;
        if (wheel != 0) _distance = Math.Max(_radius * 0.05f, _distance * MathF.Pow(0.9f, wheel / 120f));
    }

    /// <summary>Sets the camera by hand (used by the SWTOR_CAM test option). Angles are in radians.</summary>
    public void SetCamera(float yaw, float pitch, float distance, Vector3 target)
    {
        _yaw = yaw;
        _pitch = pitch;
        _distance = distance;
        _target = target;
    }

    public void ResetCamera()
    {
        _yaw = 0.6f;
        _pitch = 0.3f;
        _distance = _radius * 2.6f;
    }

    /// <summary>Draws the scene inside <paramref name="area"/> (window pixels).</summary>
    public void Draw(Rectangle area)
    {
        if (area.Width < 2 || area.Height < 2) return;
        var oldViewport = _device.Viewport;
        _device.Viewport = new Viewport(area.X, area.Y, area.Width, area.Height);
        _device.Clear(ClearOptions.DepthBuffer | ClearOptions.Target, new Color(28, 30, 36), 1f, 0);
        _device.DepthStencilState = DepthStencilState.Default;
        _device.BlendState = BlendState.Opaque;

        var view = Matrix.CreateLookAt(EyePosition(), _target, Vector3.Up);
        float near = Math.Max(_distance * 0.01f, 0.0005f);
        var projection = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(45), area.Width / (float)area.Height, near, _distance + _radius * 20f);

        if (ShowGrid && _grid.Length > 0)
        {
            _device.RasterizerState = RasterizerState.CullNone;
            _lines.View = view;
            _lines.Projection = projection;
            _lines.CurrentTechnique.Passes[0].Apply();
            _device.DrawUserPrimitives(PrimitiveType.LineList, _grid, 0, _grid.Length / 2);
        }

        _device.SamplerStates[0] = SamplerState.AnisotropicWrap;
        _device.RasterizerState = Wireframe ? Wireframe_ : CullBackfaces ? RasterizerState.CullCounterClockwise : RasterizerState.CullNone;
        _surface.View = view;
        _surface.Projection = projection;
        // Pass 0 draws solid pieces, pass 1 the pieces with holes.
        for (int pass = 0; pass < 2; pass++)
        {
            if (pass == 1) _device.BlendState = BlendState.NonPremultiplied;
            for (int m = 0; m < _meshes.Count; m++)
            {
                DrawMesh(_meshes[m], cutOut: pass == 1);
            }
        }
        _device.BlendState = BlendState.Opaque;

        _device.Viewport = oldViewport;
    }

    // Draws the pieces of a mesh that use a cut-out texture (<paramref name="cutOut"/> true) or the others.
    private void DrawMesh(GpuMesh mesh, bool cutOut)
    {
        bool bound = false;
        foreach (var piece in mesh.Pieces)
        {
            var pieceTexture = mesh.PieceTextures is not null && mesh.PieceTextures.TryGetValue(piece.MaterialIndex, out var own) ? own : mesh.Texture;
            if ((pieceTexture is not null && _cutOut.Contains(pieceTexture)) != cutOut) continue;
            if (!bound)
            {
                _device.SetVertexBuffer(mesh.Vertices);
                _device.Indices = mesh.Indices;
                bound = true;
            }
            _surface.TextureEnabled = pieceTexture is not null;
            _surface.Texture = pieceTexture;
            _surface.DiffuseColor = pieceTexture is not null ? Vector3.One : PieceColors[piece.MaterialIndex % PieceColors.Length].ToVector3();
            _surface.CurrentTechnique.Passes[0].Apply();
            _device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, piece.StartTriangle * 3, piece.TriangleCount);
        }
    }

    private static readonly RasterizerState Wireframe_ = new() { FillMode = FillMode.WireFrame, CullMode = CullMode.None };

    private Vector3 EyePosition() => _target + _distance * new Vector3(
        MathF.Cos(_pitch) * MathF.Sin(_yaw), MathF.Sin(_pitch), MathF.Cos(_pitch) * MathF.Cos(_yaw));

    // Grid on the ground plane. The spacing is a power of ten that fits the model size.
    private void BuildGrid(float groundY)
    {
        float spacing = MathF.Pow(10f, MathF.Floor(MathF.Log10(_radius)));
        const int cells = 10;
        var lines = new List<VertexPositionColor>();
        var grey = new Color(70, 74, 84);
        for (int i = -cells; i <= cells; i++)
        {
            var c = i == 0 ? new Color(120, 124, 140) : grey;
            lines.Add(new(new Vector3(i * spacing, groundY, -cells * spacing), c));
            lines.Add(new(new Vector3(i * spacing, groundY, cells * spacing), c));
            lines.Add(new(new Vector3(-cells * spacing, groundY, i * spacing), c));
            lines.Add(new(new Vector3(cells * spacing, groundY, i * spacing), c));
        }
        _grid = [.. lines];
    }

    // Averages the normals of the triangles around each vertex. Used when the file has no normals.
    private static System.Numerics.Vector3[] ComputeNormals(Gr2Mesh mesh)
    {
        var normals = new System.Numerics.Vector3[mesh.VertexCount];
        for (int i = 0; i < mesh.Indices.Length; i += 3)
        {
            int a = mesh.Indices[i], b = mesh.Indices[i + 1], c = mesh.Indices[i + 2];
            var n = System.Numerics.Vector3.Cross(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]);
            normals[a] += n;
            normals[b] += n;
            normals[c] += n;
        }
        for (int i = 0; i < normals.Length; i++)
            normals[i] = normals[i].LengthSquared() > 0 ? System.Numerics.Vector3.Normalize(normals[i]) : System.Numerics.Vector3.UnitY;
        return normals;
    }
}
