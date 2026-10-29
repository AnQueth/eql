using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using EqLog.Core.Triggers;

namespace EqLog.Overlay;

/// <summary>A choice in the alert trigger editor's Effect dropdown.</summary>
public sealed record AlertEffectOption(AlertEffect Value, string Label);

/// <summary>Animates alert text in the Alerts overlay. Looping effects run only while the text is on screen.</summary>
public static class AlertEffects
{
    public static IReadOnlyList<AlertEffectOption> Options { get; } =
    [
        new(AlertEffect.None, "None"),
        new(AlertEffect.Pulse, "Grow / shrink"),
        new(AlertEffect.Flash, "Flash"),
        new(AlertEffect.Shake, "Shake"),
        new(AlertEffect.Wobble, "Rotate (wobble)"),
        new(AlertEffect.Spin, "Spin"),
        new(AlertEffect.Bounce, "Bounce"),
        new(AlertEffect.PopIn, "Pop in"),
        new(AlertEffect.Glow, "Glow"),
    ];

    /// <summary>Starts <paramref name="effect"/> on <paramref name="target"/> whenever it is loaded and stops it when unloaded.</summary>
    public static void Attach(TextBlock target, AlertEffect effect)
    {
        if (effect == AlertEffect.None)
        {
            return;
        }

        var scale = new ScaleTransform();
        var rotate = new RotateTransform();
        var translate = new TranslateTransform();
        target.RenderTransformOrigin = new Point(0.5, 0.5);
        target.RenderTransform = new TransformGroup { Children = { scale, rotate, translate } };
        DropShadowEffect? glow = null;
        if (effect == AlertEffect.Glow)
        {
            var color = target.Foreground is SolidColorBrush brush ? brush.Color : Colors.White;
            glow = new DropShadowEffect { Color = color, ShadowDepth = 0, BlurRadius = 4, Opacity = 1 };
            target.Effect = glow;
        }

        void Start()
        {
            switch (effect)
            {
                case AlertEffect.Pulse:
                    var grow = Loop(1, 1.15, 0.45);
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                    break;
                case AlertEffect.Flash:
                    target.BeginAnimation(UIElement.OpacityProperty, Loop(1, 0.15, 0.35));
                    break;
                case AlertEffect.Shake:
                    translate.BeginAnimation(TranslateTransform.XProperty, Shake());
                    break;
                case AlertEffect.Wobble:
                    rotate.BeginAnimation(RotateTransform.AngleProperty, Loop(-6, 6, 0.35));
                    break;
                case AlertEffect.Spin:
                    rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2))
                    {
                        RepeatBehavior = RepeatBehavior.Forever,
                    });
                    break;
                case AlertEffect.Bounce:
                    translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -10, TimeSpan.FromSeconds(0.3))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                    });
                    break;
                case AlertEffect.PopIn:
                    var pop = new DoubleAnimation(0.3, 1, TimeSpan.FromSeconds(0.45))
                    {
                        EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut },
                    };
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
                    break;
                case AlertEffect.Glow:
                    glow?.BeginAnimation(DropShadowEffect.BlurRadiusProperty, Loop(4, 22, 0.6));
                    break;
            }
        }

        void Stop()
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            target.BeginAnimation(UIElement.OpacityProperty, null);
            glow?.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
        }

        target.Loaded += (_, _) => Start();
        target.Unloaded += (_, _) => Stop();
        if (target.IsLoaded)
        {
            Start();
        }
    }

    private static DoubleAnimation Loop(double from, double to, double seconds)
    {
        return new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
    }

    private static DoubleAnimationUsingKeyFrames Shake()
    {
        // A quick rattle, then a short rest, so the text stays readable.
        var shake = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(1),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        double[] offsets = [0, -7, 7, -6, 6, -4, 4, 0];
        for (int i = 0; i < offsets.Length; i++)
        {
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(offsets[i], KeyTime.FromTimeSpan(TimeSpan.FromSeconds(i * 0.05))));
        }
        return shake;
    }
}
