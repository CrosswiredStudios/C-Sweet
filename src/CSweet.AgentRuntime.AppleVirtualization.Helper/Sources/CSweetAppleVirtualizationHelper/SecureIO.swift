import CryptoKit
import Darwin
import Foundation
import Security

struct HelperPaths {
    let dataRoot: String
    let instancesRoot: String
    let managerSocketsRoot: String
    let artifactMediaRoot: String
    let kernelImagePath: String
    let brokerPort: UInt32

    static func resolve() throws -> HelperPaths {
        let environment = ProcessInfo.processInfo.environment
        let dataRoot = try absolute(environment["CSWEET_APPLE_VIRTUALIZATION_DATA_ROOT"] ??
            "/Library/Application Support/CSweet/AgentRuntime/AppleVirtualization")
        let packageRoot = try absolute(environment["CSWEET_APPLE_VIRTUALIZATION_PACKAGE_ROOT"] ??
            "/Library/Application Support/CSweet/Execution/apple-virtualization")
        let artifactRoot = try absolute(environment["CSWEET_ARTIFACT_MEDIA_ROOT"] ??
            "/Library/Application Support/CSweet/ArtifactMedia")
        let socketRoot = try absolute(environment["CSWEET_APPLE_VIRTUALIZATION_SOCKET_ROOT"] ??
            "/var/run/csweet-av")
        let portValue = environment["CSWEET_APPLE_VIRTUALIZATION_GUEST_PORT"] ?? "5000"
        guard let port = UInt32(portValue), port > 0 && port <= 65_535 else {
            throw HelperError(code: "invalid-vsock-port", message: "The Apple guest broker port is invalid.")
        }
        return HelperPaths(
            dataRoot: dataRoot,
            instancesRoot: (dataRoot as NSString).appendingPathComponent("instances"),
            managerSocketsRoot: socketRoot,
            artifactMediaRoot: artifactRoot,
            kernelImagePath: (packageRoot as NSString).appendingPathComponent("vmlinux"),
            brokerPort: port)
    }

    func instanceDirectory(_ id: UUID) throws -> String {
        try safeChild(instancesRoot, id.uuidString.replacingOccurrences(of: "-", with: "").lowercased())
    }

    private static func absolute(_ value: String) throws -> String {
        guard value.hasPrefix("/"), !value.contains("\0") else {
            throw HelperError(code: "invalid-path", message: "A configured Apple Virtualization path is invalid.")
        }
        return URL(fileURLWithPath: value).standardizedFileURL.path
    }
}

func safeChild(_ root: String, _ relative: String) throws -> String {
    guard !relative.hasPrefix("/"), !relative.contains("\0") else {
        throw HelperError(code: "invalid-path", message: "A helper path is invalid.")
    }
    let parts = relative.split(separator: "/", omittingEmptySubsequences: false)
    guard !parts.contains(where: { $0.isEmpty || $0 == "." || $0 == ".." }) else {
        throw HelperError(code: "invalid-path", message: "A helper path escaped its protected root.")
    }
    let normalizedRoot = URL(fileURLWithPath: root, isDirectory: true).standardizedFileURL.path
    let candidate = URL(fileURLWithPath: relative, relativeTo: URL(fileURLWithPath: normalizedRoot, isDirectory: true))
        .standardizedFileURL.path
    guard candidate.hasPrefix(normalizedRoot.hasSuffix("/") ? normalizedRoot : normalizedRoot + "/") else {
        throw HelperError(code: "invalid-path", message: "A helper path escaped its protected root.")
    }
    return candidate
}

func ensureProtectedDirectory(_ path: String) throws {
    try FileManager.default.createDirectory(
        atPath: path,
        withIntermediateDirectories: true,
        attributes: [.posixPermissions: 0o700])
    let attributes = try FileManager.default.attributesOfItem(atPath: path)
    guard let type = attributes[.type] as? FileAttributeType, type == .typeDirectory else {
        throw HelperError(code: "invalid-path", message: "A protected helper path is not a directory.")
    }
    try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: path)
}

func secureRandomToken() throws -> String {
    var bytes = [UInt8](repeating: 0, count: 32)
    guard SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes) == errSecSuccess else {
        throw HelperError(code: "random-failed", message: "A manager authentication token could not be generated.")
    }
    return Data(bytes).base64EncodedString()
}

func saveMetadata(_ metadata: InstanceMetadata, to path: String) throws {
    let temporary = path + "." + UUID().uuidString + ".tmp"
    try JSONEncoder.csweet.encode(metadata).write(to: URL(fileURLWithPath: temporary), options: [.atomic])
    chmod(temporary, S_IRUSR | S_IWUSR)
    if rename(temporary, path) != 0 {
        unlink(temporary)
        throw HelperError(code: "metadata-write-failed", message: "Workload metadata could not be committed.")
    }
}

func loadMetadata(from path: String) throws -> InstanceMetadata {
    let attributes = try FileManager.default.attributesOfItem(atPath: path)
    guard let size = attributes[.size] as? NSNumber, size.intValue > 1 && size.intValue <= 65_536 else {
        throw HelperError(code: "invalid-metadata", message: "Workload metadata exceeded its size limit.")
    }
    return try JSONDecoder.csweet.decode(InstanceMetadata.self, from: Data(contentsOf: URL(fileURLWithPath: path)))
}

func readBounded(_ handle: FileHandle, maximum: Int, untilNewline: Bool) throws -> Data {
    var result = Data()
    while result.count <= maximum {
        guard let chunk = try handle.read(upToCount: untilNewline ? 1 : min(8192, maximum + 1 - result.count)),
              !chunk.isEmpty else {
            if untilNewline { throw HelperError(code: "invalid-request", message: "The request ended before its delimiter.") }
            return result
        }
        if untilNewline, chunk[0] == 0x0a {
            guard !result.isEmpty else { throw HelperError(code: "invalid-request", message: "The request was empty.") }
            return result
        }
        if chunk.contains(0) || (untilNewline && chunk.contains(0x0d)) {
            throw HelperError(code: "invalid-request", message: "The request framing is invalid.")
        }
        result.append(chunk)
    }
    throw HelperError(code: "request-too-large", message: "The helper request exceeded its limit.")
}

func writeResponse(_ response: PlatformResponse, to handle: FileHandle, newline: Bool) throws {
    var data = try JSONEncoder.csweet.encode(response)
    if newline { data.append(0x0a) }
    try handle.write(contentsOf: data)
}

func createUnixListener(path: String) throws -> Int32 {
    unlink(path)
    let descriptor = socket(AF_UNIX, SOCK_STREAM, 0)
    guard descriptor >= 0 else { throw HelperError(code: "socket-failed", message: "The local manager socket could not be created.") }
    do {
        try bindUnixSocket(descriptor, path: path)
        guard chmod(path, S_IRUSR | S_IWUSR) == 0, listen(descriptor, 16) == 0 else {
            throw HelperError(code: "socket-failed", message: "The local manager socket could not be secured.")
        }
        return descriptor
    } catch {
        close(descriptor)
        unlink(path)
        throw error
    }
}

func connectUnixSocket(path: String) throws -> Int32 {
    let descriptor = socket(AF_UNIX, SOCK_STREAM, 0)
    guard descriptor >= 0 else { throw HelperError(code: "socket-failed", message: "The local manager socket could not be created.") }
    do {
        try withUnixAddress(path) { address, length in
            guard Darwin.connect(descriptor, address, length) == 0 else {
                throw HelperError(code: "manager-unavailable", message: "The workload manager is unavailable.")
            }
        }
        return descriptor
    } catch {
        close(descriptor)
        throw error
    }
}

private func bindUnixSocket(_ descriptor: Int32, path: String) throws {
    try withUnixAddress(path) { address, length in
        guard Darwin.bind(descriptor, address, length) == 0 else {
            throw HelperError(code: "socket-failed", message: "The local manager socket could not be bound.")
        }
    }
}

private func withUnixAddress<T>(
    _ path: String,
    body: (UnsafePointer<sockaddr>, socklen_t) throws -> T) throws -> T {
    let utf8 = Array(path.utf8CString)
    guard utf8.count <= MemoryLayout.size(ofValue: sockaddr_un().sun_path) else {
        throw HelperError(code: "socket-path-too-long", message: "The local manager socket path is too long.")
    }
    var address = sockaddr_un()
    address.sun_family = sa_family_t(AF_UNIX)
    withUnsafeMutablePointer(to: &address.sun_path.0) { pointer in
        _ = utf8.withUnsafeBufferPointer { source in strcpy(pointer, source.baseAddress!) }
    }
    return try withUnsafePointer(to: &address) { pointer in
        try pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
            try body($0, socklen_t(MemoryLayout<sockaddr_un>.size))
        }
    }
}

func readSocketLine(_ descriptor: Int32, maximum: Int = 4096) throws -> Data {
    var result = Data()
    var byte: UInt8 = 0
    while result.count <= maximum {
        let count = recv(descriptor, &byte, 1, 0)
        guard count == 1 else { throw HelperError(code: "manager-unavailable", message: "The manager closed its command channel.") }
        if byte == 0x0a { return result }
        guard byte != 0 && byte != 0x0d else {
            throw HelperError(code: "invalid-manager-response", message: "The manager response framing is invalid.")
        }
        result.append(byte)
    }
    throw HelperError(code: "manager-response-too-large", message: "The manager response exceeded its limit.")
}

func sendAll(_ descriptor: Int32, _ data: Data) throws {
    try data.withUnsafeBytes { rawBuffer in
        guard var pointer = rawBuffer.baseAddress?.assumingMemoryBound(to: UInt8.self) else { return }
        var remaining = rawBuffer.count
        while remaining > 0 {
            let written = Darwin.write(descriptor, pointer, remaining)
            guard written > 0 else { throw HelperError(code: "socket-write-failed", message: "The local command channel closed.") }
            pointer = pointer.advanced(by: written)
            remaining -= written
        }
    }
}

func relaySplit(input: Int32, output: Int32, peer: Int32) {
    var descriptors = [
        pollfd(fd: input, events: Int16(POLLIN), revents: 0),
        pollfd(fd: peer, events: Int16(POLLIN), revents: 0)
    ]
    var buffer = [UInt8](repeating: 0, count: 65_536)
    while poll(&descriptors, 2, -1) > 0 {
        if descriptors[0].revents & Int16(POLLIN) != 0 {
            let count = buffer.withUnsafeMutableBytes {
                Darwin.read(input, $0.baseAddress, $0.count)
            }
            if count <= 0 { break }
            if !writeAll(peer, buffer, count) { break }
        }
        if descriptors[1].revents & Int16(POLLIN) != 0 {
            let count = buffer.withUnsafeMutableBytes {
                Darwin.read(peer, $0.baseAddress, $0.count)
            }
            if count <= 0 { break }
            if !writeAll(output, buffer, count) { break }
        }
        if descriptors.contains(where: { $0.revents & Int16(POLLERR | POLLHUP | POLLNVAL) != 0 }) { break }
    }
}

func relayDuplex(_ first: Int32, _ second: Int32) {
    relaySplit(input: first, output: first, peer: second)
}

private func writeAll(_ descriptor: Int32, _ buffer: [UInt8], _ count: Int) -> Bool {
    var offset = 0
    while offset < count {
        let written = buffer.withUnsafeBytes { raw in
            Darwin.write(descriptor, raw.baseAddress!.advanced(by: offset), count - offset)
        }
        if written <= 0 { return false }
        offset += written
    }
    return true
}

func verifyArtifactISO(path: String, expectedDigest: String) -> Bool {
    guard expectedDigest.range(of: #"^sha256:[0-9a-f]{64}$"#, options: .regularExpression) != nil,
          let handle = FileHandle(forReadingAtPath: path) else { return false }
    defer { try? handle.close() }
    do {
        try handle.seek(toOffset: 16 * 2048)
        let pvd = try handle.read(upToCount: 2048) ?? Data()
        guard pvd.count == 2048, pvd[0] == 1, String(data: pvd[1..<6], encoding: .ascii) == "CD001",
              pvd[6] == 1, littleEndianUInt32(pvd, 158) == 20 else { return false }
        try handle.seek(toOffset: 20 * 2048)
        let directory = try handle.read(upToCount: 2048) ?? Data()
        var offset = 0
        while offset < directory.count && directory[offset] != 0 {
            let length = Int(directory[offset])
            guard length >= 34, offset + length <= directory.count else { return false }
            let nameLength = Int(directory[offset + 32])
            guard 33 + nameLength <= length else { return false }
            let name = String(data: directory[(offset + 33)..<(offset + 33 + nameLength)], encoding: .ascii)
            if name == "ARTIFACT.CSAB;1" {
                let extent = littleEndianUInt32(directory, offset + 2)
                let byteCount = littleEndianUInt32(directory, offset + 10)
                guard extent == 21, byteCount > 0 else { return false }
                try handle.seek(toOffset: UInt64(extent) * 2048)
                var hasher = SHA256()
                var remaining = Int(byteCount)
                while remaining > 0 {
                    let chunk = try handle.read(upToCount: min(65_536, remaining)) ?? Data()
                    guard !chunk.isEmpty else { return false }
                    hasher.update(data: chunk)
                    remaining -= chunk.count
                }
                let actual = "sha256:" + hasher.finalize().map { String(format: "%02x", $0) }.joined()
                return actual == expectedDigest
            }
            offset += length
        }
    } catch { return false }
    return false
}

private func littleEndianUInt32(_ data: Data, _ offset: Int) -> UInt32 {
    UInt32(data[offset]) | UInt32(data[offset + 1]) << 8 |
        UInt32(data[offset + 2]) << 16 | UInt32(data[offset + 3]) << 24
}
