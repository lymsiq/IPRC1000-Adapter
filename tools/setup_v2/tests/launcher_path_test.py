"""Exercise the real CMD -> Windows PowerShell quoting path; never launch the app."""
import json
import os
from pathlib import Path
import subprocess
import shutil
import tempfile

root = Path(__file__).resolve().parents[2]
with tempfile.TemporaryDirectory(prefix='dingge path regression ') as temporary:
    folder = Path(temporary) / 'IPRC1000_Adapter'
    folder.mkdir()
    with (folder / 'IPRC1000 Adapter.exe').open('wb') as file:
        file.truncate(31 * 1024 * 1024)
    (folder / 'package-manifest.json').write_text(json.dumps({'Product': 'IPRC1000_Adapter', 'SelfContained': True}), encoding='utf-8')
    probe = Path(temporary) / 'probe.cmd'
    verifier = Path(temporary) / 'check-runtime.ps1'
    shutil.copy2(root / '准备运行时.ps1', verifier)
    commands = ('@echo off\r\nsetlocal EnableExtensions\r\n'
                f'set "APPDIR={folder}\\"\r\n'
                f'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{verifier}" -CheckOnly -Quiet -AppDir "%APPDIR%."\r\n'
                'exit /b %errorlevel%\r\n')
    probe.write_bytes(commands.encode('gbk'))
    result = subprocess.run([os.environ.get('COMSPEC', 'cmd.exe'), '/d', '/c', str(probe)], capture_output=True)
    if result.returncode:
        raise RuntimeError('CMD trailing-slash regression failed: ' + result.stderr.decode('utf-8', errors='replace'))
    print('PASS real CMD / Windows PowerShell AppDir with spaces and trailing slash; no application launched')
