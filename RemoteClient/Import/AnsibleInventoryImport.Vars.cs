using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Nubis;

/// <summary>Ansible 인벤토리 옆의 group_vars/ · host_vars/ 변수 파일 읽기 (AnsibleInventoryImport 의 일부)</summary>
public static partial class AnsibleInventoryImport
{
    // ───────────────────────── group_vars · host_vars ─────────────────────────

    /// <summary>
    /// &lt;varsDir&gt;/group_vars/&lt;그룹&gt;(.yml|.yaml|.json|확장자 없음) 또는 &lt;그룹&gt;/ 폴더 안 파일들, host_vars 도 같은 규칙.
    /// 인벤토리에 나온 그룹 · 호스트 이름만 찾는다 (폴더 전체를 훑지 않음). vault 로 암호화된 파일은 건너뛴다
    /// </summary>
    private static void LoadVarsDirs(Inventory inv, string varsDir, Func<string, string?>? blockPath)
    {
        var groupDir = Path.Combine(varsDir, "group_vars");
        var hostDir = Path.Combine(varsDir, "host_vars");
        bool hasGroupDir, hasHostDir;
        try
        {
            if (blockPath?.Invoke(varsDir) != null) return;
            hasGroupDir = Directory.Exists(groupDir);
            hasHostDir = Directory.Exists(hostDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return; }
        if (!hasGroupDir && !hasHostDir) return;

        int read = 0;
        inv.GetGroup(AllGroup);   // group_vars/all.yml 은 인벤토리에 all 이 안 나와도 적용된다
        if (hasGroupDir)
            foreach (var g in inv.GroupNames())
                foreach (var file in VarsFiles(groupDir, g))
                    if (read < MaxVarsFiles && LoadVarsFile(inv, file, inv.GetGroup(g).Vars, blockPath)) read++;
        if (hasHostDir)
            foreach (var (name, vars) in inv.HostVarTargets())
                foreach (var file in VarsFiles(hostDir, name))
                    if (read < MaxVarsFiles && LoadVarsFile(inv, file, vars, blockPath)) read++;
        if (read > 0) inv.Warn(L.F("group_vars · host_vars 에서 변수 파일 {0}개를 함께 읽었습니다.", read));
    }

    /// <summary>이름에 해당하는 변수 파일 (Ansible 의 기본 확장자 규칙). 경로로 쓸 수 없는 이름은 건너뛴다</summary>
    private static List<string> VarsFiles(string dir, string name)
    {
        var files = new List<string>();
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return files;
        try
        {
            foreach (var ext in new[] { "", ".yml", ".yaml", ".json" })
            {
                var f = Path.Combine(dir, name + ext);
                if (File.Exists(f)) files.Add(f);
            }
            var sub = Path.Combine(dir, name);
            if (Directory.Exists(sub))
                files.AddRange(Directory.EnumerateFiles(sub)
                    .Where(f => Path.GetExtension(f).ToLowerInvariant() is "" or ".yml" or ".yaml" or ".json")
                    .Where(f => !Path.GetFileName(f).StartsWith('.'))
                    .OrderBy(f => f, StringComparer.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return files;
    }

    /// <summary>변수 파일 한 개를 읽어 target 에 덮어쓴다. 읽었으면 true</summary>
    private static bool LoadVarsFile(Inventory inv, string file, Dictionary<string, VarValue> target, Func<string, string?>? blockPath)
    {
        var name = Path.GetFileName(file);
        if (blockPath?.Invoke(file) is { } why) { inv.Warn(L.F("변수 파일을 건너뛰었습니다: {0} ({1})", name, why)); return false; }
        string text;
        try
        {
            if (new FileInfo(file).Length > MaxVarsFileBytes) { inv.Warn(L.F("변수 파일이 너무 커서 건너뛰었습니다: {0}", name)); return false; }
            text = ImportText.ReadFile(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            inv.Warn(L.F("변수 파일을 읽을 수 없습니다: {0} ({1})", name, ex.Message));
            return false;
        }
        if (text.TrimStart().StartsWith("$ANSIBLE_VAULT", StringComparison.Ordinal))
        {
            inv.Warn(L.F("Ansible Vault 로 암호화된 변수 파일은 건너뛰었습니다: {0}", name));
            return false;
        }
        var stream = new YamlStream();
        try { stream.Load(new StringReader(text)); }
        catch (Exception ex) when (ex is YamlException or ArgumentException or InvalidOperationException)
        {
            inv.Warn(L.F("변수 파일을 읽지 못했습니다: {0} ({1})", name, ex.Message));
            return false;
        }
        if (stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode map) ReadYamlVars(inv, map, target);
        return true;
    }
}
