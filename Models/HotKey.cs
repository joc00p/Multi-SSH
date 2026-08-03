using System.Text.Json.Serialization;
using System.Windows.Input;

namespace MultiSSH.Models;

/// <summary>
/// A user-configured hot key: pressing <see cref="Key"/> (+ <see cref="Modifiers"/>)
/// in a terminal sends <see cref="Command"/> to the remote shell. Key/Modifiers are
/// stored as enum names so the list serializes cleanly to settings.json.
/// </summary>
public class HotKey
{
    public string Key { get; set; } = "";
    public string Modifiers { get; set; } = "None";
    public string Command { get; set; } = "";
    /// <summary>Append Enter (CR) so the command runs immediately.</summary>
    public bool SendEnter { get; set; } = true;

    [JsonIgnore]
    public Key ParsedKey =>
        Enum.TryParse<Key>(Key, out var k) ? k : System.Windows.Input.Key.None;

    [JsonIgnore]
    public ModifierKeys ParsedModifiers =>
        Enum.TryParse<ModifierKeys>(Modifiers, out var m) ? m : ModifierKeys.None;

    public bool Matches(Key key, ModifierKeys mods)
        => ParsedKey != System.Windows.Input.Key.None && ParsedKey == key && ParsedModifiers == mods;

    public string Display()
    {
        if (ParsedKey == System.Windows.Input.Key.None) return "(unset)";
        var mods = ParsedModifiers;
        var parts = new List<string>();
        if ((mods & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((mods & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((mods & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((mods & ModifierKeys.Windows) != 0) parts.Add("Win");
        parts.Add(ParsedKey.ToString());
        return string.Join("+", parts);
    }

    public HotKey Clone() => (HotKey)MemberwiseClone();
}
