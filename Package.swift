// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "ComprovacaoFacilLattes",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "ComprovacaoFacilLattes",
            path: "Sources/ComprovacaoFacilLattes",
            exclude: ["Resources"],
            resources: [
                .copy("QualisData/qualis_2016_2019.tsv.gz"),
                .copy("QualisData/qualis_2017_2020.tsv.gz"),
                .copy("QualisData/qualis_2021_2024.tsv.gz"),
                .copy("Assets/AppIcon.png"),
            ],
            linkerSettings: [
                .unsafeFlags([
                    "-Xlinker", "-sectcreate",
                    "-Xlinker", "__TEXT",
                    "-Xlinker", "__info_plist",
                    "-Xlinker", "Sources/ComprovacaoFacilLattes/Resources/Info.plist"
                ])
            ]
        )
    ]
)
