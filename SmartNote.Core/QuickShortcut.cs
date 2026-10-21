namespace SmartNote.Core;

public sealed record QuickShortcut(string Text, uint Modifiers, uint Key)
{
    public bool Enabled => Key != 0;
    public static QuickShortcut Parse(string? value)
    {
        if (value is null || value.Length > 60)
            throw new InvalidDataException("ショートカットの形式が不正です。");
        if (value == "")
            return new("", 0, 0);
        var parts = value.Split('+');
        uint modifiers = 0;
        foreach (var part in parts[..^1])
        {
            uint flag = part switch
            {
                "Ctrl" => 2,
                "Alt" => 1,
                "Shift" => 4,
                _ => 0
            };
            if (flag == 0 || (modifiers & flag) != 0)
                throw new InvalidDataException("Ctrl・Alt・Shiftを重複せず指定してください。");
            modifiers |= flag;
        }
        if ((modifiers & 3) == 0)
            throw new InvalidDataException("通常の入力を妨げないよう、CtrlまたはAltを含めてください。");
        var key = parts[^1];
        uint code;
        if (key.Length == 1 && (key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9'))
            code = key[0];
        else if (key.StartsWith('F') && int.TryParse(key[1..], out var number) && number is >= 1 and <= 24 && number != 12 && key == $"F{number}")
            code = (uint)(0x70 + number - 1);
        else
            throw new InvalidDataException("英字・数字・F1～F24を指定してください。F12はWindows予約キーのため使用できません。");
        if (code == 0x73 && modifiers == 1)
            throw new InvalidDataException("Alt+F4はウィンドウを閉じる操作のため使用できません。");
        var names = new List<string>();
        if ((modifiers & 2) != 0)
            names.Add("Ctrl");
        if ((modifiers & 1) != 0)
            names.Add("Alt");
        if ((modifiers & 4) != 0)
            names.Add("Shift");
        names.Add(key);
        return new(string.Join('+', names), modifiers, code);
    }
}
