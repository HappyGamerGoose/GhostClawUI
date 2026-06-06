using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Linq;
using System.IO;
using System.IO.Compression;
using Microsoft.UI.Xaml.Controls.Primitives;
using GhostClawUI.App.Services;
using GhostClawUI.App.Ui;
using GhostClawUI.Shared;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace GhostClawUI.App.Views;

internal sealed partial class ChatView
{

    private Brush PrimaryTextBrush() => IsDarkMode ? UiKit.BrushFromHex("#F8FAFC") : ResourceBrush("TextFillColorPrimaryBrush", "#111827");


    private Brush SecondaryTextBrush() => IsDarkMode ? UiKit.BrushFromHex("#CBD5E1") : ResourceBrush("TextFillColorSecondaryBrush", "#6B7280");


    private static SolidColorBrush ChatBackgroundBrush() => new SolidColorBrush(Microsoft.UI.Colors.Transparent);


    private Brush SurfaceBrush() => IsDarkMode ? UiKit.BrushFromHex("#40252B36") : ResourceBrush("LayerFillColorDefaultBrush", "#A0FFFFFF");


    private Brush ComposerBackgroundBrush() => ResourceBrush("LayerFillColorDefaultBrush", IsDarkMode ? "#4010151C" : "#80F8FAFC");


    private Brush AssistantBubbleBrush() => ResourceBrush("CardBackgroundFillColorSecondaryBrush", IsDarkMode ? "#252B36" : "#FFFFFF");


    private Brush UserBubbleBrush() => ResourceBrush("AccentFillColorDefaultBrush", UiKit.AccentBrush.Color.ToString());


    private Brush UserBubbleBorderBrush() => ResourceBrush("AccentFillColorDefaultBrush", UiKit.AccentBrush.Color.ToString());


    private Brush ControlSurfaceBrush() => ResourceBrush("ControlFillColorDefaultBrush", IsDarkMode ? "#50303746" : "#A0FFFFFF");


    private Brush StrokeBrush() => ResourceBrush("CardStrokeColorDefaultBrush", IsDarkMode ? "#1AFFFFFF" : "#1A000000");


    private Brush SubtleBrush() => ResourceBrush("SubtleFillColorSecondaryBrush", IsDarkMode ? "#0AFFFFFF" : "#0A000000");


    private Brush CodeBackgroundBrush() => ResourceBrush("SolidBackgroundFillColorBaseBrush", IsDarkMode ? "#151A22" : "#F8FAFC");


    private Brush AccentSubtleBrush() => ResourceBrush("SystemControlTransparentBrush", IsDarkMode ? "#40172554" : "#40EFF6FF");


    private Brush ErrorSurfaceBrush() => ResourceBrush("SystemFillColorCriticalBackgroundBrush", IsDarkMode ? "#3B241C" : "#FEF3C7");


    private static SolidColorBrush GetNativeBrandBackground(string brand)
    {
        switch (brand)
        {
            case "openai":
                return UiKit.BrushFromHex("#E6F6F2");
            case "deepseek":
                return UiKit.BrushFromHex("#EEF1FF");
            case "anthropic":
                return UiKit.BrushFromHex("#FAF6F0");
            case "google":
                return UiKit.BrushFromHex("#E8F0FE");
            case "gemma":
                return UiKit.BrushFromHex("#EDE9FE");
            case "kimi":
                return UiKit.BrushFromHex("#E6FAF6");
            case "meta":
                return UiKit.BrushFromHex("#ECF3FC");
            case "mistralai":
                return UiKit.BrushFromHex("#FFF3EC");
            case "minimax":
                return UiKit.BrushFromHex("#FFEBEB");
            case "qwen":
                return UiKit.BrushFromHex("#CCFBF1");
            case "solar":
                return UiKit.BrushFromHex("#FEF9C3");
            case "nvidia":
                return UiKit.BrushFromHex("#F0FDF4");
            case "zhipu":
                return UiKit.BrushFromHex("#EFF6FF");
            case "xiaomi":
                return UiKit.BrushFromHex("#FFF0E6");
            default:
                return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 241, 245, 249)); // light slate fallback
        }
    }


    private static UIElement GetNativeBrandLogoElement(string brand, double fontSize = 12)
    {
        string? pathData = null;

        switch (brand)
        {
            case "openai":
                pathData = "M19.61 9.29a4.8 4.8 0 0 0-.28-3.79 4.9 4.9 0 0 0-3.07-2.53 4.93 4.93 0 0 0-4.88.75 4.8 4.8 0 0 0-3.32-.4 4.9 4.9 0 0 0-2.91 2.7 4.93 4.93 0 0 0-.82 4.87 4.8 4.8 0 0 0-.28 3.79 4.9 4.9 0 0 0 3.07 2.53 4.93 4.93 0 0 0 4.88-.75 4.8 4.8 0 0 0 3.32.4 4.9 4.9 0 0 0 2.91-2.7 4.93 4.93 0 0 0 .82-4.87zM11.53 4.7a3.46 3.46 0 0 1 2.65-.12 3.44 3.44 0 0 1 2.05 1.9 3.46 3.46 0 0 1-.58 3.51 3.47 3.47 0 0 1-2.65.12c-.52-.19-.9-.57-1.09-1.09a3.4 3.4 0 0 1-.33-2.32c.11-.79.52-1.5 1.1-2zM4.9 9.68a3.46 3.46 0 0 1 .58-3.51 3.44 3.44 0 0 1 2.65-.12c.79.29 1.43.9 1.76 1.69a3.45 3.45 0 0 1-.33 3.41 3.46 3.46 0 0 1-2.65.12c-.79-.29-1.43-.9-1.76-1.69a3.43 3.43 0 0 1 .33-3.41zm2.63 7.82a3.46 3.46 0 0 1-2.65.12 3.44 3.44 0 0 1-2.05-1.9 3.46 3.46 0 0 1 .58-3.51 3.47 3.47 0 0 1 2.65-.12c.52.19.9.57 1.09 1.09a3.4 3.4 0 0 1 .33 2.32 3.43 3.43 0 0 1-1.1 2zM12.47 19.3a3.46 3.46 0 0 1-2.65.12 3.44 3.44 0 0 1-2.05-1.9 3.46 3.46 0 0 1 .58-3.51 3.47 3.47 0 0 1 2.65-.12c.52.19.9.57 1.09 1.09a3.4 3.4 0 0 1 .33 2.32 3.43 3.43 0 0 1-1.1 2zm6.63-4.98a3.46 3.46 0 0 1-.58 3.51 3.44 3.44 0 0 1-2.65.12c-.79-.29-1.43-.9-1.76-1.69a3.45 3.45 0 0 1 .33-3.41 3.46 3.46 0 0 1 2.65-.12c.79.29 1.43.9 1.76 1.69a3.43 3.43 0 0 1-.33 3.41zm-2.63-7.82a3.46 3.46 0 0 1 2.65-.12 3.44 3.44 0 0 1 2.05 1.9 3.46 3.46 0 0 1-.58 3.51 3.47 3.47 0 0 1-2.65.12c-.52-.19-.9-.57-1.09-1.09a3.4 3.4 0 0 1-.33-2.32 3.43 3.43 0 0 1 1.1-2z";
                break;
            case "gemma":
            case "google":
                pathData = "M12 2c0 5.52-4.48 10-10 10 5.52 0 10 4.48 10 10 0-5.52 4.48-10 10-10-5.52 0-10-4.48-10-10z";
                break;
            case "kimi":
                pathData = "M21.846 0a1.923 1.923 0 110 3.846H20.15a.226.226 0 01-.227-.226V1.923C19.923.861 20.784 0 21.846 0z M11.065 11.199l7.257-7.2c.137-.136.06-.41-.116-.41H14.3a.164.164 0 00-.117.051l-7.82 7.756c-.122.12-.302.013-.302-.179V3.82c0-.127-.083-.23-.185-.23H3.186c-.103 0-.186.103-.186.23V19.77c0 .128.083.23.186.23h2.69c.103 0 .186-.102.186-.23v-3.25c0-.069.025-.135.069-.178l2.424-2.406a.158.158 0 01.205-.023l6.484 4.772a7.677 7.677 0 003.453 1.283c.108.012.2-.095.2-.23v-3.06c0-.117-.07-.212-.164-.227a5.028 5.028 0 01-2.027-.807l-5.613-4.064c-.117-.078-.132-.279-.028-.381z";
                break;
            case "meta":
                pathData = "M16.5 6c-1.2 0-2.3.4-3.2 1.3L12 8.5 10.7 7.3c-.9-.9-2-1.3-3.2-1.3C5 6 3 8 3 10.5S5 15 7.5 15c1.2 0 2.3-.4 3.2-1.3l1.3-1.2 1.3 1.2c.9.9 2 1.3 3.2 1.3 2.5 0 4.5-2 4.5-4.5S20 6 16.5 6zm-9 6.8c-1.3 0-2.3-1-2.3-2.3S6.2 8.2 7.5 8.2c.6 0 1.2.3 1.6.7l1.7 1.6-1.7 1.6c-.4.4-1 .7-1.6.7zm9 0c-.6 0-1.2-.3-1.6-.7l-1.7-1.6 1.7-1.6c.4-.4 1-.7 1.6-.7 1.3 0 2.3 1 2.3 2.3s-1 2.3-2.3 2.3z";
                break;
            case "mistralai":
                pathData = "M3 4l9 7 9-7v16l-9-7-9 7z";
                break;
            case "deepseek":
                pathData = "M23.748 4.482c-.254-.124-.364.113-.512.234-.051.039-.094.09-.137.136-.372.397-.806.657-1.373.626-.829-.046-1.537.214-2.163.848-.133-.782-.575-1.248-1.247-1.548-.352-.156-.708-.311-.955-.65-.172-.241-.219-.51-.305-.774-.055-.16-.11-.323-.293-.35-.2-.031-.278.136-.356.276-.313.572-.434 1.202-.422 1.84.027 1.436.633 2.58 1.838 3.393.137.093.172.187.129.323-.082.28-.18.552-.266.833-.055.179-.137.217-.329.14a5.526 5.526 0 01-1.736-1.18c-.857-.828-1.631-1.742-2.597-2.458a11.365 11.365 0 00-.689-.471c-.985-.957.13-1.743.388-1.836.27-.098.093-.432-.779-.428-.872.004-1.67.295-2.687.684a3.055 3.055 0 01-.465.137 9.597 9.597 0 00-2.883-.102c-1.885.21-3.39 1.102-4.497 2.623C.082 8.606-.231 10.684.152 12.85c.403 2.284 1.569 4.175 3.36 5.653 1.858 1.533 3.997 2.284 6.438 2.14 1.482-.085 3.133-.284 4.994-1.86.47.234.962.327 1.78.397.63.059 1.236-.03 1.705-.128.735-.156.684-.837.419-.961-2.155-1.004-1.682-.595-2.113-.926 1.096-1.296 2.746-2.642 3.392-7.003.05-.347.007-.565 0-.845-.004-.17.035-.237.23-.256a4.173 4.173 0 001.545-.475c1.396-.763 1.96-2.015 2.093-3.517.02-.23-.004-.467-.247-.588zM11.581 18c-2.089-1.642-3.102-2.183-3.52-2.16-.392.024-.321.471-.235.763.09.288.207.486.371.739.114.167.192.416-.113.603-.673.416-1.842-.14-1.897-.167-1.361-.802-2.5-1.86-3.301-3.307-.774-1.393-1.224-2.887-1.298-4.482-.02-.386.093-.522.477-.592a4.696 4.696 0 011.529-.039c2.132.312 3.946 1.265 5.468 2.774.868.86 1.525 1.887 2.202 2.891.72 1.066 1.494 2.082 2.48 2.914.348.292.625.514.891.677-.802.09-2.14.11-3.054-.614zm1-6.44a.306.306 0 01.415-.287.302.302 0 01.2.288.306.306 0 01-.31.307.303.303 0 01-.304-.308zm3.11 1.596c-.2.081-.399.151-.59.16a1.245 1.245 0 01-.798-.254c-.274-.23-.47-.358-.552-.758a1.73 1.73 0 01.016-.588c.07-.327-.008-.537-.239-.727-.187-.156-.426-.199-.688-.199a.559.559 0 01-.254-.078c-.11-.054-.2-.19-.114-.358.028-.054.16-.186.192-.21.356-.202.767-.136 1.146.016.352.144.618.408 1.001.782.391.451.462.576.685.914.176.265.336.537.445.848.067.195-.019.354-.25.452z";
                break;
            case "nvidia":
                pathData = "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 15.5c-3.03 0-5.5-2.47-5.5-5.5S10.47 6.5 13.5 6.5 19 8.97 19 12s-2.47 5.5-5.5 5.5zm0-9c-1.93 0-3.5 1.57-3.5 3.5s1.57 3.5 3.5 3.5 3.5-1.57 3.5-3.5-1.57-3.5-3.5-3.5z";
                break;
            case "solar":
                pathData = "M12 6.5c-3.03 0-5.5 2.47-5.5 5.5s2.47 5.5 5.5 5.5 5.5-2.47 5.5-5.5-2.47-5.5-5.5-5.5z";
                break;
            case "qwen":
                pathData = "M12 2C6.48 2 2 6.48 2 12c0 2.2.71 4.21 1.9 5.85L2.1 21.9l4.05-1.8c1.64 1.19 3.65 1.9 5.85 1.9 5.52 0 10-4.48 10-10S17.52 2 12 2zm0 15c-2.76 0-5-2.24-5-5s2.24-5 5-5 5 2.24 5 5-2.24 5-5 5z";
                break;
        }

        if (pathData != null)
        {
            try
            {
                var geometry = (Microsoft.UI.Xaml.Media.Geometry)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                    $"<Geometry xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">{pathData}</Geometry>"
                );

                string brandHex = brand switch
                {
                    "openai" => "#10A37F",
                    "deepseek" => "#005BFF",
                    "anthropic" => "#D97752",
                    "google" => "#4285F4",
                    "gemma" => "#4285F4",
                    "kimi" => "#222222",
                    "meta" => "#0668E1",
                    "mistralai" => "#F97316",
                    "minimax" => "#E11D48",
                    "qwen" => "#6366F1",
                    "solar" => "#EAB308",
                    "nvidia" => "#76B900",
                    "zhipu" => "#3B82F6",
                    "xiaomi" => "#FF6700",
                    _ => "#475569"
                };

                return new Microsoft.UI.Xaml.Shapes.Path
                {
                    Data = geometry,
                    Fill = UiKit.BrushFromHex(brandHex),
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                    Width = fontSize,
                    Height = fontSize,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            catch
            {
                // Fallback on parser exception
            }
        }

        // Beautiful default Fluent FontIcon fallback
        string glyph = brand switch
        {
            "openai" => "\uE9F9",
            "deepseek" => "\uE9D2",
            "anthropic" => "\uE9F9",
            "google" => "\uE8D6",
            "gemma" => "\uE8D6",
            "kimi" => "\uE9F9",
            "meta" => "\uE947",
            "mistralai" => "\uE7E7",
            "minimax" => "\uE9E9",
            "qwen" => "\uEA0B",
            "solar" => "\uE706",
            "nvidia" => "\uE781",
            "zhipu" => "\uE7C9",
            _ => "\uE9F9"
        };

        return new FontIcon
        {
            Glyph = glyph,
            FontSize = fontSize - 3 > 6 ? fontSize - 3 : 6,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }


    private async Task LoadBrandLogoAsync(string url, Image logoImage, Border logoContainer, string fallbackGlyph, string fallbackColor, string fallbackBg)
    {
        try
        {
            var bytes = await _logoHttpClient.GetByteArrayAsync(url).ConfigureAwait(false);

            logoContainer.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
                    {
                        writer.WriteBytes(bytes);
                        await writer.StoreAsync();
                        await writer.FlushAsync();
                    }

                    stream.Seek(0);

                    var prefix = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 128)).TrimStart();
                    var isSvg = prefix.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
                                 prefix.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
                                 prefix.Contains("<svg", StringComparison.OrdinalIgnoreCase);

                    if (isSvg)
                    {
                        var svgImage = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource();
                        svgImage.RasterizePixelWidth = logoImage.Width > 0 ? logoImage.Width * 2 : 128;
                        svgImage.RasterizePixelHeight = logoImage.Height > 0 ? logoImage.Height * 2 : 128;
                        logoImage.Source = svgImage;
                        logoContainer.Child = logoImage;
                        await svgImage.SetSourceAsync(stream);
                    }
                    else
                    {
                        var bitmapImage = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                        bitmapImage.DecodePixelWidth = 128;
                        bitmapImage.DecodePixelHeight = 128;
                        bitmapImage.DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Logical;
                        logoImage.Source = bitmapImage;
                        logoContainer.Child = logoImage;
                        await bitmapImage.SetSourceAsync(stream);
                    }

                    logoContainer.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    logoContainer.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                }
                catch
                {
                    SetFallbackBrandIcon(logoContainer, fallbackGlyph, fallbackColor, fallbackBg);
                }
            });
        }
        catch
        {
            logoContainer.DispatcherQueue.TryEnqueue(() =>
            {
                SetFallbackBrandIcon(logoContainer, fallbackGlyph, fallbackColor, fallbackBg);
            });
        }
    }




    private string GetBrandInfo(string modelName, out string domain, out string glyph, out string color, out string bg)
    {
        ModelClassifier.Resolve(modelName, out var brand, out var _);
        var lower = brand.ToLowerInvariant();
        glyph = "\uE9F9"; // default brain/AI

        if (lower.Contains("deepseek"))
        {
            domain = "deepseek.com";
            color = "#4D6BFE";
            bg = "#EEF1FF";
            return "deepseek";
        }
        if (lower.Contains("gpt-") || lower.Contains("o1-") || lower.Contains("o3-") || lower.Contains("openai") || lower.Contains("chatgpt"))
        {
            domain = "openai.com";
            color = "#00A67E";
            bg = "#E6F6F2";
            return "openai";
        }
        if (lower.Contains("claude") || lower.Contains("anthropic"))
        {
            domain = "anthropic.com";
            color = "#CC9966";
            bg = "#FAF6F0";
            return "anthropic";
        }
        if (lower.Contains("gemini") || lower.Contains("google"))
        {
            domain = "google.com";
            color = "#1A73E8";
            bg = "#E8F0FE";
            return "google";
        }
        if (lower.Contains("gemma"))
        {
            domain = "google.com";
            color = "#6366F1";
            bg = "#EDE9FE";
            return "gemma";
        }
        if (lower.Contains("kimi") || lower.Contains("moonshot"))
        {
            domain = "moonshot.cn";
            color = "#00A587";
            bg = "#E6FAF6";
            return "kimi";
        }
        if (lower.Contains("llama") || lower.Contains("meta"))
        {
            domain = "meta.com";
            color = "#044EAB";
            bg = "#ECF3FC";
            return "meta";
        }
        if (lower.Contains("mistral") || lower.Contains("mixtral") || lower.Contains("codestral"))
        {
            domain = "mistral.ai";
            color = "#FD5E08";
            bg = "#FFF3EC";
            return "mistralai";
        }
        if (lower.Contains("minimax"))
        {
            domain = "minimax.com";
            color = "#FF5E5B";
            bg = "#FFEBEB";
            return "minimax";
        }
        if (lower.Contains("qwen"))
        {
            domain = "qwen.ai";
            color = "#0D9488";
            bg = "#CCFBF1";
            return "qwen";
        }
        if (lower.Contains("solar") || lower.Contains("upstage"))
        {
            domain = "upstage.ai";
            color = "#EAB308";
            bg = "#FEF9C3";
            return "solar";
        }
        if (lower.Contains("nvidia"))
        {
            domain = "nvidia.com";
            color = "#76B900";
            bg = "#F0FDF4";
            return "nvidia";
        }
        if (lower.Contains("zhipu") || lower.Contains("glm"))
        {
            domain = "zhipuai.cn";
            color = "#3B82F6";
            bg = "#EFF6FF";
            return "zhipu";
        }
        if (lower.Contains("xiaomi") || lower.Contains("mimo"))
        {
            domain = "xiaomi.com";
            color = "#FF6700";
            bg = "#FFF0E6";
            return "xiaomi";
        }

        domain = "openai.com"; // default fallback for domain logos
        color = "#475569";
        bg = "#F1F5F9";
        return "default";
    }

}
