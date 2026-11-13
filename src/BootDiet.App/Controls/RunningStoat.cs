using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace BootDiet.App.Controls;

/// <summary>
/// 검사 화면의 진행 표시줄 위를 달리는 흰담비(작품 로고 캐릭터).
/// Percent만큼 오른쪽으로 가고, 검사 중에는 다리 두 동작을 번갈아 그린다.
/// 그림은 120×64 단위 좌표로 그리고 컨트롤 높이에 맞춰 늘린다. 시안 v01.
/// </summary>
public sealed class RunningStoat : Control
{
    public static readonly StyledProperty<double> PercentProperty =
        AvaloniaProperty.Register<RunningStoat, double>(nameof(Percent));

    public static readonly StyledProperty<bool> IsRunningProperty =
        AvaloniaProperty.Register<RunningStoat, bool>(nameof(IsRunning));

    public double Percent
    {
        get => GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public bool IsRunning
    {
        get => GetValue(IsRunningProperty);
        set => SetValue(IsRunningProperty, value);
    }

    private const double ArtWidth = 120, ArtHeight = 64, NoseX = 110;

    // 다리 네 개(앞 둘, 뒤 둘): 붙는 점과 각도(도, 0=아래, +는 앞쪽). A=쭉 뻗음, B=모음.
    private static readonly (double X, double Y, double Deg)[][] Legs =
    [
        [(78, 38, 40), (70, 38, 20), (36, 38, -40), (30, 37, -60)],
        [(76, 38, -15), (70, 38, -30), (38, 38, 25), (32, 38, 10)],
    ];

    private readonly DispatcherTimer timer;
    private double shown;   // 화면에 그린 진행(0~100). Percent를 부드럽게 따라간다.
    private int frame;
    private int ticks;

    public RunningStoat()
    {
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Tick());
    }

    static RunningStoat()
    {
        AffectsRender<RunningStoat>(PercentProperty, IsRunningProperty);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        shown = Percent;
        timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void Tick()
    {
        var target = Math.Clamp(Percent, 0, 100);
        var moving = Math.Abs(target - shown) > 0.05;
        shown = moving ? shown + (target - shown) * 0.12 : target;
        if (IsRunning || moving)
        {
            if (++ticks % 4 == 0)
                frame ^= 1;
            InvalidateVisual();
        }
        else if (frame != 0)
        {
            frame = 0;
            InvalidateVisual();
        }
    }

    /// <summary>진행에 맞춘 흰담비 왼쪽 끝 위치(컨트롤 좌표). 코끝이 진행 끝에 오도록 맞춘다.</summary>
    public static double LeftFor(double percent, double trackWidth, double height)
    {
        var scale = height / ArtHeight;
        var x = trackWidth * Math.Clamp(percent, 0, 100) / 100 - NoseX * scale;
        return Math.Clamp(x, 0, Math.Max(0, trackWidth - ArtWidth * scale));
    }

    public override void Render(DrawingContext context)
    {
        var h = Bounds.Height;
        if (h <= 0 || Bounds.Width <= 0)
            return;
        var scale = h / ArtHeight;
        var left = LeftFor(shown, Bounds.Width, h);
        var bob = frame == 1 ? -1.5 : 0;

        using (context.PushTransform(Matrix.CreateTranslation(0, bob) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(left, 0)))
        {
            // 로고처럼 바깥선은 한 번만: 진한 색으로 굵게 그린 뒤 그 위에 흰 몸을 덮는다.
            var outline = new Pen(Art.Ink, 5, lineJoin: PenLineJoin.Round);
            foreach (var g in new[] { Art.TailShape, Art.Body, Art.Head, Art.Ear1, Art.Ear2 })
                context.DrawGeometry(Art.Ink, outline, g);
            DrawLegs(context, new Pen(Art.Ink, 11, lineCap: PenLineCap.Round));

            context.DrawGeometry(Art.Tail, null, Art.TailShape);
            DrawLegs(context, new Pen(Brushes.White, 6, lineCap: PenLineCap.Round));
            foreach (var g in new[] { Art.Body, Art.Head, Art.Ear1, Art.Ear2 })
                context.DrawGeometry(Brushes.White, null, g);

            context.DrawEllipse(Art.Ink, null, new Point(99, 21), 2, 2.6);
            context.DrawEllipse(Art.Nose, null, new Point(109.5, 24), 1.8, 1.4);
            context.DrawEllipse(Art.Cheek, null, new Point(98, 28), 3.4, 2);
            context.DrawGeometry(null, new Pen(Art.Ink, 1.4, lineCap: PenLineCap.Round), Art.Mouth);
        }
    }


    /// <summary>그림 재료. 렌더러가 준비된 뒤(처음 그릴 때) 만든다.</summary>
    private static class Art
    {
        public static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#3D3747"));
        public static readonly IBrush Tail = new SolidColorBrush(Color.Parse("#ECE6FA"));
        public static readonly IBrush Cheek = new SolidColorBrush(Color.Parse("#F6B3C4"), 0.85);
        public static readonly IBrush Nose = new SolidColorBrush(Color.Parse("#F08AA4"));

        public static readonly Geometry Body = StreamGeometry.Parse("M24 28 C24 20 34 17 50 18 C64 19 74 17 82 19 C90 21 92 28 90 35 C88 42 78 44 64 43 C50 42 36 44 30 41 C25 39 24 34 24 28 Z");
        public static readonly Geometry Head = StreamGeometry.Parse("M80 22 C80 12 88 8 96 9 C104 10 110 16 110 23 C110 31 103 35 95 35 C87 35 80 30 80 22 Z");
        public static readonly Geometry Ear1 = StreamGeometry.Parse("M84 13 C82 8 87 5 90 8 C91 10 90 12 88 13 Z");
        public static readonly Geometry Ear2 = StreamGeometry.Parse("M93 9 C93 4 99 3 100 7 C100 9 98 10 96 10 Z");
        public static readonly Geometry TailShape = StreamGeometry.Parse("M26 30 C18 28 12 22 6 14 C4 11 7 9 10 11 C15 17 21 22 29 24 Z");
        public static readonly Geometry Mouth = StreamGeometry.Parse("M104 28 Q106 30 108 28.6");

    }

    private void DrawLegs(DrawingContext context, Pen pen)
    {
        foreach (var (x, y, deg) in Legs[frame])
        {
            var a = deg * Math.PI / 180;
            context.DrawLine(pen, new Point(x, y), new Point(x + Math.Sin(a) * 11, y + Math.Cos(a) * 11));
        }
    }
}
