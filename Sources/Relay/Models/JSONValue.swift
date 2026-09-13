import Foundation

/// Arbitrary JSON, for fields Relay carries through without interpreting.
enum JSONValue: Hashable {
    case null
    case bool(Bool)
    case number(Double)
    case string(String)
    case array([JSONValue])
    case object([String: JSONValue])

    /// From a `JSONSerialization` result.
    init(any: Any?) {
        switch any {
        case nil, is NSNull:
            self = .null
        case let n as NSNumber:
            // NSNumber hides booleans; CFBoolean is the only reliable tell.
            if CFGetTypeID(n) == CFBooleanGetTypeID() { self = .bool(n.boolValue) } else { self = .number(n.doubleValue) }
        case let s as String:
            self = .string(s)
        case let a as [Any]:
            self = .array(a.map { JSONValue(any: $0) })
        case let o as [String: Any]:
            self = .object(o.mapValues { JSONValue(any: $0) })
        default:
            self = .string(String(describing: any!))
        }
    }

    /// For `JSONSerialization`.
    var anyValue: Any {
        switch self {
        case .null:          return NSNull()
        case .bool(let b):   return b
        case .number(let n): return n.rounded() == n && abs(n) < 1e15 ? Int(n) as Any : n
        case .string(let s): return s
        case .array(let a):  return a.map(\.anyValue)
        case .object(let o): return o.mapValues(\.anyValue)
        }
    }
}

extension JSONValue: Codable {
    init(from decoder: Decoder) throws {
        if let b = try? Bool(from: decoder) { self = .bool(b) }
        else if let n = try? Double(from: decoder) { self = .number(n) }
        else if let s = try? String(from: decoder) { self = .string(s) }
        else if let a = try? [JSONValue](from: decoder) { self = .array(a) }
        else if let o = try? [String: JSONValue](from: decoder) { self = .object(o) }
        else { self = .null }
    }

    func encode(to encoder: Encoder) throws {
        switch self {
        case .null:
            var c = encoder.singleValueContainer()
            try c.encodeNil()
        case .bool(let b):   try b.encode(to: encoder)
        case .number(let n):
            if n.rounded() == n && abs(n) < 1e15 { try Int(n).encode(to: encoder) } else { try n.encode(to: encoder) }
        case .string(let s): try s.encode(to: encoder)
        case .array(let a):  try a.encode(to: encoder)
        case .object(let o): try o.encode(to: encoder)
        }
    }
}
