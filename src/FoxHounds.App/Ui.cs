using System.Numerics;
using Raylib_cs;

namespace FoxHounds.App;

/// <summary>
/// The three home-made immediate-mode widgets of spec 13.1: Button, Cycler (◀ value ▶) and Label, about 150 lines
/// instead of a raygui dependency. Raylib's built-in font has ASCII only, so arrows are drawn as triangles.
/// </summary>
internal sealed class Ui
{
    public const int FontSize = 18;

    public static readonly Color Text = new(30, 30, 30, 255);
    public static readonly Color Muted = new(110, 110, 110, 255);
    public static readonly Color Face = new(225, 225, 228, 255);
    public static readonly Color FaceHover = new(205, 210, 220, 255);
    public static readonly Color Border = new(150, 150, 155, 255);
    public static readonly Color Disabled = new(170, 170, 170, 255);

    private Vector2 _mouse;
    private bool _clicked;

    /// <summary>Reads the mouse once per frame. A click consumed by a widget is not seen by the board.</summary>
    public void BeginFrame()
    {
        _mouse = Raylib.GetMousePosition();
        _clicked = Raylib.IsMouseButtonPressed(MouseButton.Left);
    }

    public bool ClickPending => _clicked;

    public Vector2 Mouse => _mouse;

    /// <summary>Consumes this frame's click if it is inside <paramref name="r"/>.</summary>
    public bool TakeClick(Rect r)
    {
        if (_clicked && r.Contains(_mouse.X, _mouse.Y))
        {
            _clicked = false;
            return true;
        }

        return false;
    }

    public bool Button(Rect r, string label, bool enabled = true)
    {
        bool hover = enabled && r.Contains(_mouse.X, _mouse.Y);
        Raylib.DrawRectangleRec(ToRaylib(r), hover ? FaceHover : Face);
        Raylib.DrawRectangleLinesEx(ToRaylib(r), 1, Border);
        DrawCentered(label, r, enabled ? Text : Disabled);
        return enabled && TakeClick(r);
    }

    /// <summary>A labelled value with left and right arrows. Returns −1 or +1 when an arrow is clicked, else 0.</summary>
    public int Cycler(Rect r, string label, string value, bool enabled = true, float? labelWidth = null)
    {
        float labelW = labelWidth ?? Math.Min(110, r.W * 0.36f);
        Label(new Rect(r.X, r.Y, labelW, r.H), label, Muted);
        var box = new Rect(r.X + labelW, r.Y, r.W - labelW, r.H);
        Raylib.DrawRectangleRec(ToRaylib(box), Face);
        Raylib.DrawRectangleLinesEx(ToRaylib(box), 1, Border);
        var left = new Rect(box.X, box.Y, box.H, box.H);
        var right = new Rect(box.X + box.W - box.H, box.Y, box.H, box.H);
        Color arrow = enabled ? Text : Disabled;
        Triangle(left, pointRight: false, arrow, enabled && left.Contains(_mouse.X, _mouse.Y));
        Triangle(right, pointRight: true, arrow, enabled && right.Contains(_mouse.X, _mouse.Y));
        DrawCentered(Fit(value, (int)(box.W - (2 * box.H))), box, enabled ? Text : Disabled);
        if (!enabled)
        {
            return 0;
        }

        return TakeClick(left) ? -1 : TakeClick(right) ? 1 : 0;
    }

    public static void Label(Rect r, string text, Color color, int fontSize = FontSize)
    {
        Raylib.DrawText(Fit(text, (int)r.W, fontSize), (int)r.X, (int)(r.Y + ((r.H - fontSize) / 2)), fontSize, color);
    }

    public static void DrawCentered(string text, Rect r, Color color, int fontSize = FontSize)
    {
        int w = Raylib.MeasureText(text, fontSize);
        Raylib.DrawText(text, (int)(r.X + ((r.W - w) / 2)), (int)(r.Y + ((r.H - fontSize) / 2)), fontSize, color);
    }

    /// <summary>Shortens text with "..." until it fits <paramref name="width"/> pixels.</summary>
    public static string Fit(string text, int width, int fontSize = FontSize)
    {
        if (Raylib.MeasureText(text, fontSize) <= width)
        {
            return text;
        }

        for (int len = text.Length - 1; len > 0; len--)
        {
            string candidate = string.Concat(text.AsSpan(0, len), "...");
            if (Raylib.MeasureText(candidate, fontSize) <= width)
            {
                return candidate;
            }
        }

        return "";
    }

    /// <summary>
    /// Breaks text at spaces into at most <paramref name="maxLines"/> lines that fit <paramref name="width"/> pixels;
    /// when text is left over, the last line ends with "...".
    /// </summary>
    public static List<string> Wrap(string text, int width, int fontSize, int maxLines)
    {
        var lines = new List<string>();
        string current = "";
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (Raylib.MeasureText(candidate, fontSize) <= width || current.Length == 0)
            {
                current = candidate;
                continue;
            }

            if (lines.Count == maxLines - 1)
            {
                lines.Add(Fit(current + " ...", width, fontSize));
                return lines;
            }

            lines.Add(current);
            current = word;
        }

        if (current.Length > 0)
        {
            lines.Add(Fit(current, width, fontSize));
        }

        return lines;
    }

    public static Rectangle ToRaylib(Rect r) => new(r.X, r.Y, r.W, r.H);

    private static void Triangle(Rect r, bool pointRight, Color color, bool hover)
    {
        if (hover)
        {
            Raylib.DrawRectangleRec(ToRaylib(r.Inset(2)), FaceHover);
        }

        float cx = r.X + (r.W / 2);
        float cy = r.Y + (r.H / 2);
        float s = r.H * 0.22f;

        // Raylib draws triangles whose vertices are in counter-clockwise order.
        if (pointRight)
        {
            Raylib.DrawTriangle(new Vector2(cx - s, cy - s), new Vector2(cx - s, cy + s), new Vector2(cx + s, cy), color);
        }
        else
        {
            Raylib.DrawTriangle(new Vector2(cx + s, cy - s), new Vector2(cx - s, cy), new Vector2(cx + s, cy + s), color);
        }
    }
}
