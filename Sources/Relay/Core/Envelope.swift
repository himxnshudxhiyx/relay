import CryptoKit
import Foundation
import Security

/// The Livekeeping encrypted-body scheme: a fresh AES-256-GCM key encrypts the
/// JSON, RSA-OAEP-SHA256 wraps that key for the other side, and the nonce is
/// base64 of 12 bytes with the GCM tag appended to the ciphertext.
///
/// Requests go out as `{requestData, encryptedKey, nonce, raw}`, where `raw` is
/// the sender's public key; the server answers `{responseData, encryptedKey, nonce}`
/// wrapped for that key, so only the sender can read the reply.
enum Envelope {
    /// An already-encrypted body (copied from the app) with `raw` swapped for
    /// our own public key, so the server encrypts its reply to us. The rest is
    /// untouched: the server still decrypts `requestData` with its own key.
    static func rekey(_ wire: String) throws -> (wire: String, key: SecKey)? {
        guard var json = (try? JSONSerialization.jsonObject(with: Data(wire.utf8))) as? [String: Any],
              json["raw"] is String, json["encryptedKey"] is String else { return nil }
        let mine = try generateKey()
        guard let myPublic = SecKeyCopyPublicKey(mine) else { throw HTTPError(message: "Couldn't create an encryption key.") }
        json["raw"] = try spki(myPublic).base64EncodedString()
        let data = try JSONSerialization.data(withJSONObject: json, options: [.prettyPrinted, .withoutEscapingSlashes])
        return (String(decoding: data, as: UTF8.self), mine)
    }

    /// The decrypted `responseData`, or nil when the body isn't an envelope for `key`.
    static func open(_ body: Data, key: SecKey) -> String? {
        guard let json = try? JSONSerialization.jsonObject(with: body) as? [String: Any] else { return nil }
        return try? decrypt(json, with: key, field: "responseData")
    }

    // MARK: - Primitives

    /// The server's side of the scheme; Relay only uses it in tests.
    static func encrypt(_ plain: String, to publicKey: SecKey, field: String) throws -> [String: String] {
        let aes = SymmetricKey(size: .bits256)
        let nonce = AES.GCM.Nonce()
        let box = try AES.GCM.seal(Data(plain.utf8), using: aes, nonce: nonce)
        let wrapped = try rsa(SecKeyCreateEncryptedData, publicKey, aes.withUnsafeBytes { Data($0) })
        return [field: (box.ciphertext + box.tag).base64EncodedString(),
                "encryptedKey": wrapped.base64EncodedString(),
                "nonce": Data(nonce).base64EncodedString()]
    }

    static func decrypt(_ json: [String: Any], with privateKey: SecKey, field: String) throws -> String {
        guard let payload = (json[field] as? String).flatMap({ Data(base64Encoded: $0) }), payload.count >= 16,
              let wrapped = (json["encryptedKey"] as? String).flatMap({ Data(base64Encoded: $0) }),
              let nonce = (json["nonce"] as? String).flatMap({ Data(base64Encoded: $0) }) else {
            throw HTTPError(message: "Not an encrypted body.")
        }
        let aes = SymmetricKey(data: try rsa(SecKeyCreateDecryptedData, privateKey, wrapped))
        let box = try AES.GCM.SealedBox(nonce: AES.GCM.Nonce(data: nonce),
                                        ciphertext: payload.dropLast(16), tag: payload.suffix(16))
        return String(decoding: try AES.GCM.open(box, using: aes), as: UTF8.self)
    }

    static func generateKey() throws -> SecKey {
        var error: Unmanaged<CFError>?
        let attributes: [CFString: Any] = [kSecAttrKeyType: kSecAttrKeyTypeRSA, kSecAttrKeySizeInBits: 2048]
        guard let key = SecKeyCreateRandomKey(attributes as CFDictionary, &error) else { throw error!.takeRetainedValue() }
        return key
    }

    /// SecKey exports an RSA public key as bare PKCS#1; servers want it wrapped
    /// in SubjectPublicKeyInfo. This header is fixed for 2048-bit keys.
    static func spki(_ publicKey: SecKey) throws -> Data {
        var error: Unmanaged<CFError>?
        guard let pkcs1 = SecKeyCopyExternalRepresentation(publicKey, &error) as Data? else { throw error!.takeRetainedValue() }
        let header: [UInt8] = [0x30, 0x82, 0x01, 0x22, 0x30, 0x0d, 0x06, 0x09, 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d,
                               0x01, 0x01, 0x01, 0x05, 0x00, 0x03, 0x82, 0x01, 0x0f, 0x00]
        return Data(header) + pkcs1
    }

    private static func rsa(_ op: (SecKey, SecKeyAlgorithm, CFData, UnsafeMutablePointer<Unmanaged<CFError>?>?) -> CFData?,
                            _ key: SecKey, _ data: Data) throws -> Data {
        var error: Unmanaged<CFError>?
        guard let out = op(key, .rsaEncryptionOAEPSHA256, data as CFData, &error) as Data? else {
            throw error?.takeRetainedValue() ?? HTTPError(message: "RSA failed.")
        }
        return out
    }

}
