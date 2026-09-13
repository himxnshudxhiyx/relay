// Renders Relay's app icon — two opposing arrows, a request out and a
// response back — into an .iconset folder.
import AppKit

let out = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "build/Relay.iconset"
try? FileManager.default.createDirectory(atPath: out, withIntermediateDirectories: true)

func color(_ hex: UInt32, _ alpha: CGFloat = 1) -> CGColor {
    CGColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255, alpha: alpha)
}

func render(_ pixels: Int) -> Data {
    // An explicit bitmap, so each PNG is exactly the pixel size iconutil expects
    // regardless of the display's backing scale.
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels,
                               bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                               colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let ctx = NSGraphicsContext.current!.cgContext

    let s = CGFloat(pixels)
    let inset = s * 0.085
    let rect = CGRect(x: inset, y: inset, width: s - inset * 2, height: s - inset * 2)
    let tile = CGPath(roundedRect: rect, cornerWidth: s * 0.225, cornerHeight: s * 0.225, transform: nil)

    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -s * 0.012), blur: s * 0.03, color: color(0x000000, 0.25))
    ctx.addPath(tile)
    ctx.setFillColor(color(0x4338CA))
    ctx.fillPath()
    ctx.restoreGState()

    ctx.saveGState()
    ctx.addPath(tile)
    ctx.clip()
    let gradient = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(),
                              colors: [color(0x8B7CFF), color(0x4338CA)] as CFArray, locations: [0, 1])!
    ctx.drawLinearGradient(gradient, start: CGPoint(x: rect.minX, y: rect.maxY),
                           end: CGPoint(x: rect.maxX, y: rect.minY), options: [])
    ctx.restoreGState()

    ctx.setLineWidth(s * 0.058)
    ctx.setLineCap(.round)
    ctx.setLineJoin(.round)
    let left = rect.minX + rect.width * 0.25
    let right = rect.maxX - rect.width * 0.25
    let head = rect.width * 0.105

    // Request: →
    let upper = rect.midY + rect.height * 0.115
    ctx.setStrokeColor(color(0xFFFFFF))
    ctx.move(to: CGPoint(x: left, y: upper))
    ctx.addLine(to: CGPoint(x: right, y: upper))
    ctx.move(to: CGPoint(x: right - head, y: upper + head))
    ctx.addLine(to: CGPoint(x: right, y: upper))
    ctx.addLine(to: CGPoint(x: right - head, y: upper - head))
    ctx.strokePath()

    // Response: ←
    let lower = rect.midY - rect.height * 0.115
    ctx.setStrokeColor(color(0xFFFFFF, 0.62))
    ctx.move(to: CGPoint(x: right, y: lower))
    ctx.addLine(to: CGPoint(x: left, y: lower))
    ctx.move(to: CGPoint(x: left + head, y: lower + head))
    ctx.addLine(to: CGPoint(x: left, y: lower))
    ctx.addLine(to: CGPoint(x: left + head, y: lower - head))
    ctx.strokePath()

    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

for (size, name) in [(16, "16x16"), (32, "16x16@2x"), (32, "32x32"), (64, "32x32@2x"),
                     (128, "128x128"), (256, "128x128@2x"), (256, "256x256"), (512, "256x256@2x"),
                     (512, "512x512"), (1024, "512x512@2x")] {
    try? render(size).write(to: URL(fileURLWithPath: "\(out)/icon_\(name).png"))
}
print("iconset written to \(out)")
