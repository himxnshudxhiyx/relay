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
    static let accent  = Color.dynamic(light: 0x5B4FE9, dark: 0x8F88FF)
    static let canvas  = Color.dynamic(light: 0xFFFFFF, dark: 0x1D1D20)
    static let panel   = Color.dynamic(light: 0xF9F9FB, dark: 0x19191C)
    static let chrome  = Color.dynamic(light: 0xF2F2F5, dark: 0x141416)
    static let field   = Color.dynamic(light: 0xF4F4F7, dark: 0x28282C)
    static let line    = Color.dynamic(light: 0xE3E3E8, dark: 0x323238)
    static let hover   = Color.primary.opacity(0.06)

    static let green   = Color.dynamic(light: 0x15803D, dark: 0x4ADE80)
    static let amber   = Color.dynamic(light: 0xB45309, dark: 0xFBBF24)
    static let blue    = Color.dynamic(light: 0x1D4ED8, dark: 0x60A5FA)
    static let purple  = Color.dynamic(light: 0x7E22CE, dark: 0xC084FC)
    static let red     = Color.dynamic(light: 0xB91C1C, dark: 0xF87171)
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
