import Darwin
import Foundation

signal(SIGPIPE, SIG_IGN)

if CommandLine.arguments.count == 3, CommandLine.arguments[1] == "--workload-host" {
    guard #available(macOS 14.0, *) else { exit(EXIT_FAILURE) }
    do {
        try VirtualMachineManager(
            metadataPath: CommandLine.arguments[2], paths: HelperPaths.resolve()).run()
    } catch {
        exit(EXIT_FAILURE)
    }
}

var responseCommitted = false
do {
    let arguments = try parseArguments(Array(CommandLine.arguments.dropFirst()))
    guard arguments.protocolVersion == helperProtocolVersion else {
        throw HelperError(code: "unsupported-protocol", message: "The helper protocol version is unsupported.")
    }
    guard #available(macOS 14.0, *) else {
        throw HelperError(code: "unsupported-host", message: "Apple Virtualization requires macOS 14 or later.")
    }
    let payload = try readBounded(
        FileHandle.standardInput, maximum: 1_048_576,
        untilNewline: arguments.operation == "open-guest-channel")
    let request = payload.isEmpty
        ? PlatformRequest()
        : try JSONDecoder.csweet.decode(PlatformRequest.self, from: payload)
    let controller = HelperController(paths: try HelperPaths.resolve())
    if arguments.operation == "open-guest-channel" {
        let manager = try controller.openGuestChannel(request)
        defer { close(manager) }
        let handshakeData = try readSocketLine(manager)
        let handshake = try JSONDecoder.csweet.decode(PlatformResponse.self, from: handshakeData)
        try writeResponse(handshake, to: .standardOutput, newline: true)
        responseCommitted = true
        if handshake.success && handshake.guestChannelTransport == guestChannelTransport {
            relaySplit(input: STDIN_FILENO, output: STDOUT_FILENO, peer: manager)
        }
    } else {
        let response = controller.execute(arguments.operation, request: request)
        try writeResponse(response, to: .standardOutput, newline: false)
        responseCommitted = true
    }
} catch let error as HelperError {
    if !responseCommitted {
        try? writeResponse(.failure(error.code, error.message), to: .standardOutput, newline: false)
    }
} catch {
    if !responseCommitted {
        try? writeResponse(
            .failure("helper-failure", "The Apple Virtualization helper failed unexpectedly."),
            to: .standardOutput, newline: false)
    }
}

private struct Arguments {
    var protocolVersion: String
    var operation: String
}

private func parseArguments(_ values: [String]) throws -> Arguments {
    let allowed = Set(["probe", "create", "start", "inspect", "stop", "destroy", "reap", "logs", "open-guest-channel"])
    guard values.count == 4 else {
        throw HelperError(code: "invalid-arguments", message: "The helper arguments are incomplete.")
    }
    var protocolVersion: String?
    var operation: String?
    for index in stride(from: 0, to: values.count, by: 2) {
        switch values[index] {
        case "--protocol" where protocolVersion == nil: protocolVersion = values[index + 1]
        case "--operation" where operation == nil: operation = values[index + 1]
        default: throw HelperError(code: "invalid-arguments", message: "The helper received an unsupported argument.")
        }
    }
    guard let protocolVersion, let operation, allowed.contains(operation) else {
        throw HelperError(code: "invalid-arguments", message: "The helper arguments are invalid.")
    }
    return Arguments(protocolVersion: protocolVersion, operation: operation)
}
