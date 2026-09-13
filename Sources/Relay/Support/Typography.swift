import AppKit
import SwiftUI

/// Poppins, bundled with the app, for all interface text. Code — URLs, bodies,
/// headers — stays monospaced, because JSON in a proportional face is
/// unreadable.
///
/// Poppins ships as separate files per weight rather than a variable font, so
/// SwiftUI's `.weight()` would do nothing — the weight has to pick the face.
enum Typography {
    static func face(for weight: Font.Weight) -> String {
        switch weight {
        case .black, .heavy, .bold: return "Poppins-Bold"
        case .semibold:             return "Poppins-SemiBold"
        case .medium:               return "Poppins-Medium"
        default:                    return "Poppins-Regular"
        }
    }
}

extension Font {
    /// `fixedSize` so layouts tuned to exact point sizes stay put.
    static func app(_ size: CGFloat, _ weight: Font.Weight = .regular) -> Font {
        .custom(Typography.face(for: weight), fixedSize: size)
    }

    static func code(_ size: CGFloat = 12, _ weight: Font.Weight = .regular) -> Font {
        .system(size: size, weight: weight, design: .monospaced)
    }
}

extension NSFont {
    static func app(_ size: CGFloat, _ weight: Font.Weight = .regular) -> NSFont {
        NSFont(name: Typography.face(for: weight), size: size) ?? .systemFont(ofSize: size)
    }

    static func code(_ size: CGFloat = 12) -> NSFont {
        .monospacedSystemFont(ofSize: size, weight: .regular)
    }
}
