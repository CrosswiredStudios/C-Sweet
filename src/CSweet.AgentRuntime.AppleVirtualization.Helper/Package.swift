// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "CSweet.AgentRuntime.AppleVirtualization.Helper",
    platforms: [.macOS(.v14)],
    products: [
        .executable(
            name: "CSweet.AgentRuntime.AppleVirtualization.Helper",
            targets: ["CSweetAppleVirtualizationHelper"])
    ],
    targets: [
        .executableTarget(
            name: "CSweetAppleVirtualizationHelper",
            path: "Sources/CSweetAppleVirtualizationHelper")
    ]
)
