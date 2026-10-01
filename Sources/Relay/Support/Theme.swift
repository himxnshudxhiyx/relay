import AppKit
import SwiftUI

extension NSColor {
    convenience init(hex: UInt32) {
        self.init(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255,
                  green: CGFloat((hex >> 8) & 0xFF) / 255,
                  blue: CGFloat(hex & 0xFF) / 255,
                  alpha: 1)
    }

    /// Resolves against the view's appearance at draw time, so one token
    /// serves light and dark without views checking the colour scheme.
    static func dynamic(light: UInt32, dark: UInt32) -> NSColor {
        NSColor(name: nil) { appearance in
            appearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? NSColor(hex: dark) : NSColor(hex: light)
        }
    }
}

extension Color {
    static func dynamic(light: UInt32, dark: UInt32) -> Color {
        Color(nsColor: .dynamic(light: light, dark: dark))
    }
}

enum Theme {
    // Carbon + lime. `accent` is for text and underlines (darkened in light
    // mode to stay readable); `accentFill` is the bright lime behind dark text.
    static let accent     = Color.dynamic(light: 0x4A6600, dark: 0xC6F432)
    static let accentFill = Color.dynamic(light: 0xC6F432, dark: 0xC6F432)
    static let onAccent   = Color.dynamic(light: 0x0B0B0B, dark: 0x0B0B0B)
    static let accentSoft = Color.dynamic(light: 0xEDF8C6, dark: 0x1F2709)

    static let canvas   = Color.dynamic(light: 0xFFFFFF, dark: 0x0B0B0B)
    static let panel    = canvas
    static let chrome   = Color.dynamic(light: 0xF5F5F4, dark: 0x070707)
    static let field    = Color.dynamic(light: 0xFAFAFA, dark: 0x131313)
    static let editor   = Color.dynamic(light: 0xFCFCFC, dark: 0x0F0F0F)
    static let selected = Color.dynamic(light: 0xEBEBEA, dark: 0x1C1C1C)
    static let line     = Color.dynamic(light: 0xE6E6E4, dark: 0x222222)
    static let hover    = Color.primary.opacity(0.05)

    static let green   = Color.dynamic(light: 0x0F8A55, dark: 0x5BD69B)
    static let amber   = Color.dynamic(light: 0xA86F00, dark: 0xF5C451)
    static let blue    = Color.dynamic(light: 0x1F6FD1, dark: 0x62B3F5)
    static let purple  = Color.dynamic(light: 0x7B4FE0, dark: 0xB79CF7)
    static let red     = Color.dynamic(light: 0xD33A2F, dark: 0xF2675F)
    static let teal    = Color.dynamic(light: 0x0F766E, dark: 0x2DD4BF)
    static let pink    = Color.dynamic(light: 0xBE185D, dark: 0xF472B6)

    static func method(_ method: String) -> Color {
        switch method.uppercased() {
        case "GET":     return green
        case "POST":    return amber
        case "PUT":     return blue
        case "PATCH":   return purple
        case "DELETE":  return red
        case "HEAD":    return teal
        case "OPTIONS": return pink
        default:        return .secondary
        }
    }

    static func status(_ code: Int) -> Color {
        switch code {
        case 200..<300: return green
        case 300..<400: return blue
        case 400..<500: return amber
        case 500...:    return red
        default:        return .secondary
        }
    }

    static func variable(_ source: VariableScope.Source) -> Color {
        switch source {
        case .environment: return green
        case .collection:  return blue
        case .dynamic:     return purple
        case .missing:     return red
        }
    }
}
