import hashlib
import json
import shutil
import zipfile
from pathlib import Path

WORK = Path(__file__).resolve().parent.parent
ROOT = WORK.parent
VERSION = "1.7.5"
SOURCE = WORK / "source"
TOOLS = WORK / "tools"
PLUGIN = ROOT / f"CoordinateTriggerEvents_{VERSION}_plugin_package"
SOURCE_PACKAGE = ROOT / f"CoordinateTriggerEvents_{VERSION}_source_package"
DLL = SOURCE / "bin/Release/net6.0/CoordinateTriggerEvents.dll"

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def main():
    assert ROOT.resolve() == Path(r"D:\CodexWorkspaces\GTFO").resolve()
    for path in (PLUGIN, SOURCE_PACKAGE, Path(str(PLUGIN)+".zip"), Path(str(SOURCE_PACKAGE)+".zip")):
        assert not path.exists(), f"Refusing to overwrite {path}"
    assert DLL.is_file() and sha(DLL)
    assert "1.7.5" in (SOURCE / "Core/Plugin.cs").read_text(encoding="utf-8")
    assert "<Version>1.7.5</Version>" in (SOURCE / "CoordinateTriggerEvents.csproj").read_text(encoding="utf-8")
    for number in (1, 2, 3):
        review = json.loads((TOOLS / f"review_round_{number}.json").read_text(encoding="utf-8"))
        assert review["passed"] and all(item["passed"] for item in review["checks"])
        for relative, expected in review["source_sha256"].items():
            assert sha(SOURCE / relative).upper() == expected.upper(), relative
    manifest = {"name":"CoordinateTriggerEvents","version_number":VERSION,"version":VERSION,"plugin_guid":"com.gtfo.coordinatetriggerevents","plugin_dll":"BepInEx/plugins/CoordinateTriggerEvents.dll","description":"GTFO BepInEx plugin for coordinate, scan, big pickup, and HUD interact trigger events."}
    for package in (PLUGIN, SOURCE_PACKAGE):
        package.mkdir()
        (package/"manifest.json").write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
        shutil.copy2(WORK/"README_1.7.5_CN.md",package/"README_1.7.5_CN.md")
        shutil.copy2(WORK/"BUGFIX_MEMORY.md",package/"BUGFIX_MEMORY.md")
    destination=PLUGIN/"BepInEx/plugins/CoordinateTriggerEvents.dll"
    destination.parent.mkdir(parents=True)
    shutil.copy2(DLL,destination)
    shutil.copytree(SOURCE,SOURCE_PACKAGE/"source",ignore=shutil.ignore_patterns("bin","obj"))
    (SOURCE_PACKAGE/"tools").mkdir()
    shutil.copytree(TOOLS/"BigPickupIcon.Tests",SOURCE_PACKAGE/"tools/BigPickupIcon.Tests",ignore=shutil.ignore_patterns("bin","obj"))
    for number in (1,2,3): shutil.copy2(TOOLS/f"review_round_{number}.json",SOURCE_PACKAGE/"tools")
    shutil.copy2(WORK/"build.ps1",SOURCE_PACKAGE/"build.ps1")
    report=f"# CoordinateTriggerEvents {VERSION} Build Report\n\n- Plugin DLL SHA256: {sha(DLL)}\n- Three pre-build source reviews: passed.\n- BigPickup icon regression assertions: passed.\n- In-game verification: pending user confirmation.\n- Local failure context: BUGFIX_MEMORY.md.\n"
    for package in (PLUGIN,SOURCE_PACKAGE): (package/f"BUILD_REPORT_{VERSION}_CN.md").write_text(report,encoding="utf-8")
    outputs=[]
    for package in (PLUGIN,SOURCE_PACKAGE):
        archive_path=Path(str(package)+".zip")
        with zipfile.ZipFile(archive_path,"w",zipfile.ZIP_DEFLATED,compresslevel=9) as archive:
            for path in sorted(package.rglob("*")):
                if path.is_file(): archive.write(path,path.relative_to(package).as_posix())
        with zipfile.ZipFile(archive_path) as archive: assert archive.testzip() is None
        outputs.append({"path":str(archive_path),"sha256":sha(archive_path),"bytes":archive_path.stat().st_size})
    print(json.dumps(outputs,ensure_ascii=False,indent=2))

if __name__ == "__main__": main()
