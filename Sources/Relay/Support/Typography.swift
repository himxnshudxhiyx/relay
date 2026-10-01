import AppKit
import SwiftUI

/// The system face for interface text. Code — URLs, bodies, headers — stays
/// monospaced, because JSON in a proportional face is unreadable.
extension Font {
    /// Exact point sizes, so layouts tuned to them stay put.
    static func app(_ size: CGFloat, _ weight: Font.Weight = .regular) -> Font {
        .system(size: size, weight: weight)
    }

    static func code(_ size: CGFloat = 12, _ weight: Font.Weight = .regular) -> Font {
        .system(size: size, weight: weight, design: .monospaced)
    }
}

extension NSFont {
    static func app(_ size: CGFloat, _ weight: Font.Weight = .regular) -> NSFont {
        let w: NSFont.Weight
        switch weight {
        case .black, .heavy, .bold: w = .bold
        case .semibold:             w = .semibold
        case .medium:               w = .medium
        default:                    w = .regular
        }
        return .systemFont(ofSize: size, weight: w)
    }

    static func code(_ size: CGFloat = 12) -> NSFont {
        .monospacedSystemFont(ofSize: size, weight: .regular)
    }
}
