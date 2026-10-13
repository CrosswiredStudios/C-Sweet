"""Verify artifact handoffs against packed dependencies, without publishing or modifying live work."""
import argparse
import json
from pathlib import Path
import subprocess
import sys
import zipfile
import xml.etree.ElementTree as ET

REPOSITORIES = [
    "ArtDirector.VideoGame", "Artist.VideoGame", "AudioDesigner.VideoGame",
    "BuildReleaseEngineer.VideoGame", "CreativeDirector.VideoGame", "Engineer.VideoGame",
    "GameDesigner", "LevelDesigner.VideoGame", "NarrativeDesigner.VideoGame",
    "PlaytestResearcher.VideoGame", "Producer.VideoGame", "QA.VideoGame", "SoftwareDeveloper", "SoftwareQA",
    "TechnicalArtist.VideoGame", "TechnicalDirector.VideoGame", "UiUxAccessibilityDesigner.VideoGame",
]

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repos", nargs="*", choices=REPOSITORIES, default=REPOSITORIES)
    args = parser.parse_args()
    platform = Path(__file__).resolve().parents[1]
    output = platform / "artifacts/artifact-delivery"
    feed = output / "feed"
    cache = output / "consumer-cache"
    output.mkdir(parents=True, exist_ok=True)
    flags = ["-p:UseLocalCSweetAgentSdk=false", "-p:UseLocalCSweetWorkManagementContracts=false",
             "-p:NuGetAudit=false", f"-p:RestorePackagesPath={cache}"]
    report_path = output / "verification.json"
    results = json.loads(report_path.read_text(encoding="utf-8")) if report_path.exists() else []
    for name in args.repos:
        repo = platform.parent / ("CSweet.Agent." + name)
        manifest = json.loads((repo / "csweet-plugin.json").read_text(encoding="utf-8-sig"))
        project = repo / manifest["runtime"]["projectPath"]
        solution = next(repo.glob("*.sln*"))
        version = manifest["version"]
        notes = repo / f"releases/{version}.md"
        if not notes.exists() or notes.read_text(encoding="utf-8-sig").splitlines()[0] != f"# {version}":
            raise RuntimeError(f"Release notes do not match {repo.name} {version}")
        result = {"repository": repo.name, "version": version}
        commands = {
            "restore": ["dotnet", "restore", str(solution), "--source", str(feed),
                        "--source", str(Path.home() / ".nuget/packages"), *flags],
            "test": ["dotnet", "test", str(solution), "--no-restore", "-c", "Release", "-m:1", *flags],
            "self-test": ["dotnet", "run", "--project", str(project), "--no-build", "--no-restore",
                          "-c", "Release", *flags, "--", "--self-test"],
            "pack": ["dotnet", "pack", str(project), "--no-build", "--no-restore", "-c", "Release",
                     "-o", str(feed), *flags],
        }
        with (output / (name + ".log")).open("w", encoding="utf-8") as log:
            for stage, command in commands.items():
                log.write("\n" + stage + "\n"); log.flush()
                result[stage] = subprocess.run(command, cwd=repo, stdout=log, stderr=subprocess.STDOUT).returncode
                if result[stage]: break
        if result.get("pack") == 0:
            package_id = ET.parse(project).findtext(".//PackageId") or project.stem
            package = feed / f"{package_id}.{version}.nupkg"
            with zipfile.ZipFile(package) as archive:
                nuspec = next(x for x in archive.namelist() if x.endswith(".nuspec"))
                metadata = ET.fromstring(archive.read(nuspec))
                packed_version = next(x.text for x in metadata.iter() if x.tag.split("}")[-1] == "version")
                if packed_version != version: raise RuntimeError(f"Wrong package version: {package}")
            result["verifiedPackage"] = package.name
        results = [previous for previous in results if previous["repository"] != result["repository"]]
        results.append(result)
        report_path.write_text(json.dumps(results, indent=2), encoding="utf-8")
        print(json.dumps(result), flush=True)
    return int(any(any(result.get(stage, 1) for stage in commands) for result in results))

if __name__ == "__main__":
    sys.exit(main())
