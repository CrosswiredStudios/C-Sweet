import Darwin
import Foundation
import Virtualization

@available(macOS 14.0, *)
final class VirtualMachineManager: NSObject, VZVirtualMachineDelegate {
    private let metadataPath: String
    private var metadata: InstanceMetadata
    private let queue = DispatchQueue(label: "com.csweet.apple-virtualization.vm")
    private var virtualMachine: VZVirtualMachine!
    private var terminalError: String?

    init(metadataPath: String, paths: HelperPaths) throws {
        guard geteuid() == 0 else {
            throw HelperError(code: "privilege-required", message: "The workload host requires the protected system identity.")
        }
        self.metadataPath = metadataPath
        metadata = try loadMetadata(from: metadataPath)
        let instanceName = metadata.instanceId.uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        let expectedDirectory = try paths.instanceDirectory(metadata.instanceId)
        guard metadataPath == (try safeChild(expectedDirectory, "instance.json")),
              metadata.scratchImagePath == (try safeChild(expectedDirectory, "scratch.raw")),
              metadata.managerSocketPath == (try safeChild(paths.managerSocketsRoot, instanceName + ".sock")),
              metadata.kernelImagePath == URL(fileURLWithPath: paths.kernelImagePath).standardizedFileURL.path,
              metadata.managerToken.utf8.count >= 32 && metadata.managerToken.utf8.count <= 256 else {
            throw HelperError(code: "invalid-metadata", message: "The workload metadata escaped its protected instance.")
        }
        super.init()
        virtualMachine = VZVirtualMachine(configuration: try makeConfiguration(), queue: queue)
        virtualMachine.delegate = self
    }

    func run() throws -> Never {
        let listener = try createUnixListener(path: metadata.managerSocketPath)
        defer {
            close(listener)
            unlink(metadata.managerSocketPath)
        }
        while true {
            let client = accept(listener, nil, nil)
            if client < 0 {
                if errno == EINTR { continue }
                throw HelperError(code: "manager-failed", message: "The workload manager could not accept a command.")
            }
            autoreleasepool {
                handle(client)
                close(client)
            }
        }
    }

    private func handle(_ client: Int32) {
        do {
            let requestData = try readSocketLine(client)
            let request = try JSONDecoder.csweet.decode(ManagerRequest.self, from: requestData)
            guard fixedTimeEqual(request.token, metadata.managerToken) else {
                throw HelperError(code: "manager-authentication-failed", message: "The manager command was not authenticated.")
            }
            switch request.operation {
            case "start":
                try start()
                try sendResponse(.ok(), client)
            case "inspect":
                try sendResponse(PlatformResponse(success: true, status: status()), client)
            case "stop":
                try stop(gracePeriod: request.gracePeriodSeconds ?? 0)
                try sendResponse(.ok(), client)
            case "destroy":
                try stop(gracePeriod: 0)
                metadata.finishedAt = metadata.finishedAt ?? Date()
                try saveMetadata(metadata, to: metadataPath)
                try sendResponse(.ok(), client)
                exit(EXIT_SUCCESS)
            case "open-guest-channel":
                let connection = try connectGuestChannel()
                try sendResponse(.ok(transport: true), client)
                relayDuplex(client, connection.fileDescriptor)
            default:
                throw HelperError(code: "invalid-operation", message: "The workload manager operation is not supported.")
            }
        } catch let error as HelperError {
            try? sendResponse(.failure(error.code, error.message), client)
        } catch {
            try? sendResponse(.failure("manager-operation-failed", "The workload manager operation failed."), client)
        }
    }

    private func makeConfiguration() throws -> VZVirtualMachineConfiguration {
        try metadata.resources.validate()
        for path in [metadata.kernelImagePath, metadata.guestImagePath] {
            guard FileManager.default.isReadableFile(atPath: path) else {
                throw HelperError(code: "image-unavailable", message: "An immutable virtual machine image is unavailable.")
            }
        }
        let configuration = VZVirtualMachineConfiguration()
        configuration.platform = VZGenericPlatformConfiguration()
        let bootLoader = VZLinuxBootLoader(kernelURL: URL(fileURLWithPath: metadata.kernelImagePath))
        bootLoader.commandLine = "console=hvc0 panic=-1 reboot=t root=/dev/vda ro csweet.broker_port=\(metadata.brokerPort)"
        configuration.bootLoader = bootLoader
        configuration.cpuCount = max(VZVirtualMachineConfiguration.minimumAllowedCPUCount,
            min(metadata.resources.virtualCpuCount, VZVirtualMachineConfiguration.maximumAllowedCPUCount))
        let memoryBytes = UInt64(metadata.resources.memoryMegabytes) * 1_048_576
        guard memoryBytes >= VZVirtualMachineConfiguration.minimumAllowedMemorySize,
              memoryBytes <= VZVirtualMachineConfiguration.maximumAllowedMemorySize else {
            throw HelperError(code: "invalid-resources", message: "The requested memory is unsupported on this host.")
        }
        configuration.memorySize = memoryBytes
        configuration.entropyDevices = [VZVirtioEntropyDeviceConfiguration()]
        configuration.socketDevices = [VZVirtioSocketDeviceConfiguration()]
        configuration.networkDevices = []

        var storage: [VZStorageDeviceConfiguration] = []
        storage.append(try blockDevice(metadata.guestImagePath, readOnly: true))
        storage.append(try blockDevice(metadata.scratchImagePath, readOnly: false))
        if let artifact = metadata.artifactImagePath {
            storage.append(try blockDevice(artifact, readOnly: true))
        }
        configuration.storageDevices = storage
        try configuration.validate()
        return configuration
    }

    private func blockDevice(_ path: String, readOnly: Bool) throws -> VZVirtioBlockDeviceConfiguration {
        let attachment = try VZDiskImageStorageDeviceAttachment(
            url: URL(fileURLWithPath: path), readOnly: readOnly,
            cachingMode: readOnly ? .cached : .automatic,
            synchronizationMode: readOnly ? .none : .full)
        return VZVirtioBlockDeviceConfiguration(attachment: attachment)
    }

    private func start() throws {
        let result: Result<Void, Error> = waitOnQueue { completion in
            switch self.virtualMachine.state {
            case .running: completion(.success(()))
            case .stopped: self.virtualMachine.start(completionHandler: completion)
            default: completion(.failure(HelperError(
                code: "invalid-state", message: "The virtual machine cannot be started from its current state.")))
            }
        }
        try result.get()
        metadata.startedAt = metadata.startedAt ?? Date()
        try saveMetadata(metadata, to: metadataPath)
    }

    private func stop(gracePeriod: Int) throws {
        let current = queue.sync { virtualMachine.state }
        guard current != .stopped else { return }
        if gracePeriod > 0 {
            let requested = queue.sync { virtualMachine.canRequestStop }
            if requested {
                try queue.sync { try virtualMachine.requestStop() }
                let deadline = Date().addingTimeInterval(TimeInterval(min(gracePeriod, 300)))
                while Date() < deadline {
                    if queue.sync(execute: { virtualMachine.state == .stopped }) { break }
                    Thread.sleep(forTimeInterval: 0.1)
                }
            }
        }
        if queue.sync(execute: { virtualMachine.state != .stopped }) {
            let result: Result<Void, Error> = waitOnQueue { completion in
                self.virtualMachine.stop { error in
                    if let error { completion(.failure(error)) }
                    else { completion(.success(())) }
                }
            }
            try result.get()
        }
        metadata.finishedAt = metadata.finishedAt ?? Date()
        try saveMetadata(metadata, to: metadataPath)
    }

    private func connectGuestChannel() throws -> VZVirtioSocketConnection {
        guard queue.sync(execute: { virtualMachine.state == .running }),
              let device = virtualMachine.socketDevices.first as? VZVirtioSocketDevice else {
            throw HelperError(code: "not-running", message: "The workload is not running.")
        }
        let result: Result<VZVirtioSocketConnection, Error> = waitOnQueue { completion in
            device.connect(toPort: self.metadata.brokerPort, completionHandler: completion)
        }
        return try result.get()
    }

    private func status() -> WorkloadStatus {
        let snapshot = queue.sync { virtualMachine.state }
        let state: Int
        switch snapshot {
        case .stopped: state = metadata.startedAt == nil ? 1 : 6
        case .starting: state = 2
        case .running, .paused, .pausing, .resuming: state = 4
        case .stopping: state = 5
        case .error: state = 9
        default: state = 0
        }
        return WorkloadStatus(
            handle: metadata.handle,
            state: state,
            terminationReason: terminalError == nil ? 0 : 10,
            exitCode: nil,
            startedAt: metadata.startedAt,
            finishedAt: metadata.finishedAt,
            errorCode: terminalError == nil ? nil : "virtual-machine-failed",
            sanitizedError: terminalError)
    }

    private func waitOnQueue<T>(_ operation: @escaping (@escaping (Result<T, Error>) -> Void) -> Void) -> Result<T, Error> {
        let semaphore = DispatchSemaphore(value: 0)
        var result: Result<T, Error>!
        queue.async {
            operation {
                result = $0
                semaphore.signal()
            }
        }
        semaphore.wait()
        return result
    }

    private func sendResponse(_ response: PlatformResponse, _ client: Int32) throws {
        var data = try JSONEncoder.csweet.encode(response)
        data.append(0x0a)
        try sendAll(client, data)
    }

    func virtualMachine(_ virtualMachine: VZVirtualMachine, didStopWithError error: Error) {
        terminalError = "The virtual machine stopped unexpectedly."
        metadata.finishedAt = metadata.finishedAt ?? Date()
        try? saveMetadata(metadata, to: metadataPath)
    }

    func guestDidStop(_ virtualMachine: VZVirtualMachine) {
        metadata.finishedAt = metadata.finishedAt ?? Date()
        try? saveMetadata(metadata, to: metadataPath)
    }
}

private func fixedTimeEqual(_ first: String, _ second: String) -> Bool {
    let lhs = Array(first.utf8)
    let rhs = Array(second.utf8)
    guard lhs.count == rhs.count else { return false }
    var difference: UInt8 = 0
    for index in lhs.indices { difference |= lhs[index] ^ rhs[index] }
    return difference == 0
}
