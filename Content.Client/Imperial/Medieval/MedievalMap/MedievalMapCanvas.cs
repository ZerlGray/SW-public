using System.Numerics;
using Content.Shared.Imperial.Medieval.MedievalMap;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Input;

namespace Content.Client.Imperial.Medieval.MedievalMap;

/// <summary>Ink annotations on the existing illustrated map; positions are intentionally chosen by a player.</summary>
public sealed class MedievalMapCanvas : TextureRect
{
    public List<MedievalMapAnnotation> Annotations = new();
    public bool CanAnnotate;
    public int SelectedAnnotation = -1;
    public Action<Vector2>? OnPlaceAnnotation;
    public Action<int>? OnSelectAnnotation;
    public Action<float>? OnZoom;

    public MedievalMapCanvas()
    {
        MouseFilter = MouseFilterMode.Stop;
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);
        if (Texture == null) return;
        var bounds = GetDrawDimensions(Texture);
        var font = UserInterfaceManager.ThemeDefaults.DefaultFont;
        for (var i = 0; i < Annotations.Count; i++)
        {
            var point = bounds.TopLeft + Annotations[i].Position * bounds.Size;
            handle.DrawCircle(point, 11 * UIScale, Color.Black);
            handle.DrawCircle(point, 9 * UIScale, i == SelectedAnnotation ? Color.Gold : Color.DarkRed);
            handle.DrawString(font, point + new Vector2(-4, 5) * UIScale, (i + 1).ToString(), UIScale, Color.White);
        }
    }

    protected override void KeyBindDown(GUIBoundKeyEventArgs args)
    {
        base.KeyBindDown(args);
        if (args.Function != EngineKeyFunctions.UIClick || Texture == null)
            return;

        var point = args.RelativePixelPosition;
        var bounds = GetDrawDimensions(Texture);
        if (bounds.Width <= 0 || bounds.Height <= 0 || !bounds.Contains(point)) return;
        if (CanAnnotate)
        {
            OnPlaceAnnotation?.Invoke((point - bounds.TopLeft) / bounds.Size);
            args.Handle();
            return;
        }

        for (var i = 0; i < Annotations.Count; i++)
        {
            if (Vector2.Distance(point, bounds.TopLeft + Annotations[i].Position * bounds.Size) > 16 * UIScale)
                continue;
            OnSelectAnnotation?.Invoke(i);
            args.Handle();
            return;
        }
    }

    protected override void MouseWheel(GUIMouseWheelEventArgs args)
    {
        base.MouseWheel(args);
        OnZoom?.Invoke(args.Delta.Y);
        args.Handle();
    }
}
