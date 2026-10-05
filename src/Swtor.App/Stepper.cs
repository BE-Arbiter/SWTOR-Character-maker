using ImGuiNET;

namespace Swtor.App;

/// <summary>
/// "&lt;" and "&gt;" buttons around a combo box, to go to the previous or the next choice.
/// Use: <c>int step = Stepper.Before(id); ...combo...; step += Stepper.After(id);</c>, then
/// <c>Stepper.Move</c> gives the new position.
/// </summary>
internal static class Stepper
{
    private const float ButtonWidth = 22;

    /// <summary>
    /// Draws "&lt;" and sets the width of the next item so that "&gt;" fits after it.
    /// </summary>
    /// <param name="width">Width of the combo. 0 fills the line.</param>
    /// <param name="reserveRight">Space to keep at the right of "&gt;" (for example for a check box).</param>
    /// <returns>-1 when the button was clicked, otherwise 0.</returns>
    public static int Before(string id, float width = 0, float reserveRight = 0)
    {
        int step = ImGui.Button($"<##prev{id}", new System.Numerics.Vector2(ButtonWidth, 0)) ? -1 : 0;
        ImGui.SameLine(0, ImGui.GetStyle().ItemSpacing.X / 2);
        ImGui.SetNextItemWidth(width > 0 ? width : -(ButtonWidth + ImGui.GetStyle().ItemSpacing.X / 2 + reserveRight));
        return step;
    }

    /// <summary>Draws "&gt;" after the combo. Returns 1 when clicked, otherwise 0.</summary>
    public static int After(string id)
    {
        ImGui.SameLine(0, ImGui.GetStyle().ItemSpacing.X / 2);
        return ImGui.Button($">##next{id}", new System.Numerics.Vector2(ButtonWidth, 0)) ? 1 : 0;
    }

    /// <summary>
    /// New position after a step. The list wraps around. With <paramref name="allowNone"/>, position -1 means "nothing"
    /// and sits between the last and the first choice.
    /// </summary>
    /// <param name="current">Position now, or -1 for nothing (or when the current choice is not in the list).</param>
    /// <returns>A position from 0 to count - 1, or -1 for nothing.</returns>
    public static int Move(int count, int current, int step, bool allowNone)
    {
        if (count <= 0) return -1;
        int slots = allowNone ? count + 1 : count;
        int at = allowNone ? current + 1 : Math.Max(current, 0);
        int moved = ((at + step) % slots + slots) % slots;
        return allowNone ? moved - 1 : moved;
    }
}
