using System.Text;
using Swtor.Formats;
using Swtor.Formats.Xml;

namespace Swtor.Tests;

public class AppearanceReaderTests
{
    private const string IndexXml = """
        <Assets>
          <Asset>
            <ID>1</ID>
            <ArtName>chest_a</ArtName>
            <BaseFile>/art/dynamic/chest/model/chest_[bt]_base.gr2</BaseFile>
            <Attachments>
              <Attachment id="10" name="x" filename="/art/dynamic/chest/model/chest_[bt]_arm.gr2" />
              <Attachment id="11" name="y" filename="/art/dynamic/chest/model/chest_[bt]_back.gr2" />
            </Attachments>
            <Materials>
              <Material id="5" name="m1" filename="/art/shaders/materials/a_c01_u.mat">
                <ColorSchemes><ColorScheme guid="9" /></ColorSchemes>
                <MaterialOverrides />
              </Material>
              <Material id="6" name="m2" filename="/art/shaders/materials/a_c02_[gen].mat"><ColorSchemes /></Material>
            </Materials>
            <Bodytypes><Bodytype>bfa</Bodytype><Bodytype>bma</Bodytype></Bodytypes>
            <SkinMaterialIndex>1</SkinMaterialIndex>
            <CustomData><SkinMaterials><SkinMaterial slot="chest" filename="/art/shaders/materials/chest_naked_x_[bt].mat" /></SkinMaterials></CustomData>
          </Asset>
          <Asset>
            <ID>2</ID>
            <ArtName>chest_b</ArtName>
            <BaseFile>/art/dynamic/chest/model/other.gr2</BaseFile>
            <Attachments />
            <Materials />
            <Bodytypes />
          </Asset>
        </Assets>
        """;

    [Fact]
    public void Read_IndexXml_ReturnsAllFields()
    {
        var assets = AppearanceIndexReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(IndexXml)));

        Assert.Equal(2, assets.Count);
        var first = assets[0];
        Assert.Equal(("1", "chest_a"), (first.Id, first.ArtName));
        Assert.Equal("/art/dynamic/chest/model/chest_[bt]_base.gr2", first.BaseFile);
        Assert.Equal(2, first.Attachments.Count);
        Assert.Equal(["bfa", "bma"], first.Bodytypes);
        Assert.Equal(["a_c01_u", "a_c02_[gen]"], first.Materials.Select(m => Path.GetFileNameWithoutExtension(m.FileName)));
        Assert.Equal("other.gr2", Path.GetFileName(assets[1].BaseFile));
        Assert.Empty(assets[1].Materials);
        Assert.Equal(1, first.SkinMaterialIndex);
        Assert.Equal(-1, assets[1].SkinMaterialIndex);
        Assert.Equal("/art/shaders/materials/chest_naked_x_[bt].mat", first.SkinMaterials?["chest"]);
        Assert.Null(assets[1].SkinMaterials);
    }

    [Fact]
    public void Read_InvalidXml_Throws()
    {
        Assert.Throws<GameFormatException>(() => AppearanceIndexReader.Read(new MemoryStream("<Assets><Asset>"u8.ToArray())));
    }

    [Fact]
    public void Parse_Material_NormalizesTexturePaths()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Material>
              <Derived>Garment</Derived>
              <AlphaMode>Test</AlphaMode>
              <AlphaTestValue>0.25</AlphaTestValue>
              <input><semantic>DiffuseMap</semantic><type>texture</type><value>\art\dynamic\x\tex_d</value></input>
              <input><semantic>GlossMap</semantic><type>texture</type><value>art\dynamic\x\tex_s</value></input>
              <input><semantic>RimWidth</semantic><type>float</type><value>1.5</value></input>
            </Material>
            """;
        var material = MaterialReader.Parse("﻿" + xml);

        Assert.Equal("Garment", material.Shader);
        Assert.Equal("art/dynamic/x/tex_d", material.DiffuseMap);
        Assert.Equal("art/dynamic/x/tex_s", material.TexturePath("GlossMap"));
        Assert.Null(material.TexturePath("Missing"));
        Assert.Equal(("Test", 0.25f), (material.AlphaMode, material.AlphaTestValue));
    }
}
