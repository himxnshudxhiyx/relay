using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Relay.Core;

namespace Relay.App;

/// <summary>Colours code needs outside XAML (syntax, methods, status), in light and dark. Same values as Theme.axaml.</summary>
static class Palette
{
    static bool Dark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
    static IBrush Pick(uint light, uint dark) => new ImmutableSolidColorBrush(Color.FromUInt32(0xFF000000 | (Dark ? dark : light)));

    public static IBrush Key => Pick(0x4A6600, 0xE2F59A);
    public static IBrush String => Pick(0x4F7A00, 0xB5E35A);
    public static IBrush Number => Pick(0xB06A00, 0xF5C451);
    public static IBrush Literal => Pick(0x1F6FD1, 0x62B3F5);
    public static IBrush Punctuation => Pick(0x858585, 0x6E6E6E);
    public static IBrush Comment => Pick(0x9A9A9A, 0x5A5A5A);
    public static IBrush LineNumber => Pick(0xBDBDBD, 0x3F3F3F);
    public static IBrush Accent => Pick(0x4A6600, 0xC6F432);
    public static IBrush Text => Pick(0x111111, 0xEDEDED);
    public static IBrush Secondary => Pick(0x5C5C5C, 0x9C9C9C);

    public static IBrush Green => Pick(0x0F8A55, 0x5BD69B);
    public static IBrush Amber => Pick(0xA86F00, 0xF5C451);
    public static IBrush Blue => Pick(0x1F6FD1, 0x62B3F5);
    public static IBrush Purple => Pick(0x7B4FE0, 0xB79CF7);
    public static IBrush Red => Pick(0xD33A2F, 0xF2675F);
    public static IBrush Teal => Pick(0x0F766E, 0x2DD4BF);
    public static IBrush Pink => Pick(0xBE185D, 0xF472B6);

    public static IBrush Method(string method) => method.ToUpperInvariant() switch
    {
        "GET" => Green,
        "POST" => Amber,
        "PUT" => Blue,
        "PATCH" => Purple,
        "DELETE" => Red,
        "HEAD" => Teal,
        "OPTIONS" => Pink,
        _ => Secondary,
    };

    public static string ShortMethod(string method) => method.ToUpperInvariant() switch
    {
        "DELETE" => "DEL",
        "OPTIONS" => "OPT",
        var m => m,
    };

    public static IBrush Status(int code) => code switch
    {
        >= 200 and < 300 => Green,
        >= 300 and < 400 => Blue,
        >= 400 and < 500 => Amber,
        >= 500 => Red,
        _ => Secondary,
    };

    public static IBrush Variable(VariableScope.Source source) => source switch
    {
        VariableScope.Source.Environment => Green,
        VariableScope.Source.Collection => Blue,
        VariableScope.Source.Dynamic => Purple,
        _ => Red,
    };

    public static IBrush Tint(IBrush brush, double opacity) =>
        brush is ISolidColorBrush s ? new ImmutableSolidColorBrush(s.Color, opacity) : brush;
}

/// <summary>Small XAML converters.</summary>
static class Conv
{
    public static readonly IValueConverter MethodBrush = new FuncValueConverter<string?, IBrush>(m => Palette.Method(m ?? ""));
    public static readonly IValueConverter MethodShort = new FuncValueConverter<string?, string>(m => Palette.ShortMethod(m ?? ""));
    public static readonly IValueConverter NotNull = new FuncValueConverter<object?, bool>(o => o != null);
    public static readonly IValueConverter NotEmpty = new FuncValueConverter<string?, bool>(s => !string.IsNullOrEmpty(s));
    public static readonly IValueConverter Not = new FuncValueConverter<bool, bool>(b => !b);

    /// <summary>True when the value equals the parameter (an enum name), for "selected" classes.</summary>
    public static readonly IValueConverter Is = new EqualsConverter();

    sealed class EqualsConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true && parameter is string name && targetType.IsEnum ? Enum.Parse(targetType, name) : Avalonia.Data.BindingOperations.DoNothing;
    }
}
