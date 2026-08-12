import Foundation

let providerID = "apple-virtualization"
let guestChannelTransport = "stdio-duplex-v1"
let helperProtocolVersion = "1.0"

struct PlatformRequest: Decodable {
    var builderWorkload: WorkloadSpec? = nil
    var runtimeWorkload: WorkloadSpec? = nil
    var handle: WorkloadHandle? = nil
    var guestImagePath: String? = nil
    var artifactImagePath: String? = nil
    var gracePeriodSeconds: Int? = nil
    var maximumBytes: Int? = nil

    var singleWorkload: WorkloadSpec? {
        if builderWorkload != nil && runtimeWorkload != nil { return nil }
        return builderWorkload ?? runtimeWorkload
    }
}

struct WorkloadSpec: Decodable {
    var workloadId: UUID
    var kind: Int
    var resourceLimits: ResourceLimits
    var brokerLease: BrokerLease
    var artifact: ArtifactReference?
}

struct ResourceLimits: Codable {
    var virtualCpuCount: Int
    var cpuPercent: Int
    var memoryMegabytes: Int
    var writableDiskMegabytes: Int
    var maximumProcessCount: Int
    var maximumLogBytes: Int
    var maximumDuration: String?

    func validate() throws {
        guard (1...64).contains(virtualCpuCount), (1...6400).contains(cpuPercent),
              (128...1_048_576).contains(memoryMegabytes),
              (64...1_048_576).contains(writableDiskMegabytes),
              (1...1_000_000).contains(maximumProcessCount),
              (1...1_073_741_824).contains(maximumLogBytes) else {
            throw HelperError(code: "invalid-resources", message: "The workload resource limits are invalid.")
        }
    }
}

struct BrokerLease: Decodable {
    var expiresAt: Date
}

struct ArtifactReference: Decodable {
    var digest: String
}

struct WorkloadHandle: Codable, Equatable {
    var providerId: String
    var workloadId: UUID
    var providerInstanceId: String
    var kind: Int
}

struct WorkloadStatus: Codable {
    var handle: WorkloadHandle
    var state: Int
    var terminationReason: Int
    var exitCode: Int?
    var startedAt: Date?
    var finishedAt: Date?
    var errorCode: String?
    var sanitizedError: String?
}

struct PlatformResponse: Codable {
    var success: Bool
    var errorCode: String? = nil
    var sanitizedError: String? = nil
    var providerInstanceId: String? = nil
    var status: WorkloadStatus? = nil
    var logs: [LogChunk]? = nil
    var workloadsRemoved: Int? = nil
    var guestChannelTransport: String? = nil

    static func ok(transport: Bool = false) -> PlatformResponse {
        PlatformResponse(success: true, guestChannelTransport: transport ? guestChannelTransport : nil)
    }

    static func failure(_ code: String, _ message: String) -> PlatformResponse {
        PlatformResponse(success: false, errorCode: code, sanitizedError: message)
    }
}

struct LogChunk: Codable {
    var occurredAt: Date
    var stream: String
    var content: Data
    var isTruncated: Bool
}

struct InstanceMetadata: Codable {
    var instanceId: UUID
    var workloadId: UUID
    var kind: Int
    var managerPid: Int32
    var managerSocketPath: String
    var managerToken: String
    var guestImagePath: String
    var artifactImagePath: String?
    var kernelImagePath: String
    var scratchImagePath: String
    var resources: ResourceLimits
    var brokerPort: UInt32
    var createdAt: Date
    var startedAt: Date?
    var finishedAt: Date?
    var leaseExpiresAt: Date?

    var handle: WorkloadHandle {
        WorkloadHandle(
            providerId: providerID,
            workloadId: workloadId,
            providerInstanceId: instanceId.uuidString.replacingOccurrences(of: "-", with: "").lowercased(),
            kind: kind)
    }
}

struct ManagerRequest: Codable {
    var token: String
    var operation: String
    var gracePeriodSeconds: Int?
}

struct HelperError: Error {
    let code: String
    let message: String
}

extension JSONDecoder {
    static var csweet: JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .custom { decoder in
            let container = try decoder.singleValueContainer()
            let value = try container.decode(String.self)
            guard let date = CSweetDateCodec.decode(value) else {
                throw DecodingError.dataCorruptedError(
                    in: container,
                    debugDescription: "Invalid ISO-8601 timestamp.")
            }
            return date
        }
        return decoder
    }
}

extension JSONEncoder {
    static var csweet: JSONEncoder {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .custom { date, encoder in
            var container = encoder.singleValueContainer()
            try container.encode(CSweetDateCodec.encode(date))
        }
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        return encoder
    }
}

private enum CSweetDateCodec {
    private static let fractional: ISO8601DateFormatter = {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter
    }()

    private static let whole: ISO8601DateFormatter = {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        return formatter
    }()

    static func decode(_ value: String) -> Date? {
        fractional.date(from: value) ?? whole.date(from: value)
    }

    static func encode(_ date: Date) -> String {
        fractional.string(from: date)
    }
}
