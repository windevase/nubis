using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nubis;

/// <summary>클러스터를 어디서 가져왔는지</summary>
public enum K8sSource { Imported, Cloud }

/// <summary>클러스터 인증 방식 (카드 배지·필요한 플러그인 안내)</summary>
public enum K8sAuthKind { Kubeconfig, Aws, Gcp, Azure, Oci, Exec }

/// <summary>
/// 쿠버네티스 클러스터 하나. 인증은 그 클러스터만 담은 독립 kubeconfig 를 DPAPI 로 암호화해 들고 있다 —
/// 클러스터마다 인증이 따로라 다른 클러스터의 토큰이 섞이지 않는다. 저장 파일(k8s_clusters.json)도 파일 전체 암호화.
/// </summary>
public sealed class K8sCluster
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>화면 이름 (사용자가 바꿀 수 있음)</summary>
    public string Name { get; set; } = "";
    /// <summary>직접 등록한 클러스터의 그룹 ("" = 그룹 없음). 클라우드 클러스터는 프로필 그룹</summary>
    public string Group { get; set; } = "";
    public K8sSource Source { get; set; } = K8sSource.Imported;
    public K8sAuthKind AuthKind { get; set; } = K8sAuthKind.Kubeconfig;

    /// <summary>kubeconfig 안의 context 이름 (kubectl --context 로 고정)</summary>
    public string ContextName { get; set; } = "";
    /// <summary>API 서버 주소 (표시용)</summary>
    public string Endpoint { get; set; } = "";
    /// <summary>기본 네임스페이스 ("" = kubeconfig 의 값 / default)</summary>
    public string DefaultNamespace { get; set; } = "";
    public string Version { get; set; } = "";

    // ----- 클라우드에서 가져온 클러스터 -----
    public Guid? CloudProfileId { get; set; }
    /// <summary>제공자별 접두어가 붙은 ID (eks:arn, gke:selfLink, aks:id, oke:ocid, tke:id) — VM 인스턴스 ID 와 겹치지 않게</summary>
    public string? CloudClusterId { get; set; }
    public string? Region { get; set; }
    /// <summary>클라우드 상태를 정규화한 값 ("running" 이면 정상)</summary>
    public string? State { get; set; }
    /// <summary>마지막으로 받은 클라우드 이름. Name 이 이것과 다르면 사용자가 바꾼 것 — 동기화가 덮어쓰지 않는다</summary>
    public string? CloudName { get; set; }
    /// <summary>빠짐없는 동기화에서 연속으로 보이지 않은 횟수 (두 번이면 지운다 — 목록 API 가 잠깐 빠뜨려도 바로 지우지 않게)</summary>
    public int MissCount { get; set; }

    public bool Favorite { get; set; }
    public DateTime? LastConnected { get; set; }
    public Dictionary<string, string>? Tags { get; set; }

    /// <summary>독립 kubeconfig (DPAPI). 결과·로그·MCP 에 절대 넣지 않는다</summary>
    public string? ProtectedKubeconfig { get; set; }

    [JsonIgnore]
    public string Kubeconfig
    {
        get => Secret.Unprotect(ProtectedKubeconfig);
        set => ProtectedKubeconfig = string.IsNullOrEmpty(value) ? null : Secret.Protect(value);
    }

    [JsonIgnore] public bool IsCloud => CloudProfileId != null;
    [JsonIgnore] public bool IsStopped => IsCloud && !string.IsNullOrEmpty(State) && !State.Equals("running", StringComparison.OrdinalIgnoreCase);

    /// <summary>표시용 네임스페이스 (비어 있으면 default)</summary>
    [JsonIgnore] public string NamespaceOrDefault => string.IsNullOrWhiteSpace(DefaultNamespace) ? "default" : DefaultNamespace;

    /// <summary>인증 방식 배지 글자</summary>
    [JsonIgnore]
    public string AuthBadge => AuthKind switch
    {
        K8sAuthKind.Aws => "aws",
        K8sAuthKind.Gcp => "gcloud",
        K8sAuthKind.Azure => "azure",
        K8sAuthKind.Oci => "oci",
        K8sAuthKind.Exec => "exec",
        _ => "kubeconfig",
    };

    /// <summary>얕은 복사 + 태그 사본 (편집용 — 원본은 저장 성공 뒤에만 바꾼다)</summary>
    public K8sCluster Clone()
    {
        var c = (K8sCluster)MemberwiseClone();
        c.Tags = Tags == null ? null : new Dictionary<string, string>(Tags, StringComparer.OrdinalIgnoreCase);
        return c;
    }
}

/// <summary>k8s_clusters.json 저장소 (VaultStore 와 같은 방식, 파일 전체 DPAPI)</summary>
public static class K8sStore
{
    private static readonly object Gate = new();
    private static List<K8sCluster>? _clusters;

    public const string FileName = "k8s_clusters.json";
    public static string FilePath => Path.Combine(AppPaths.DataDir, FileName);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>파일을 읽지 못해(.bak 복구도 실패) 빈 목록으로 시작했는지</summary>
    public static bool LoadFailed { get; private set; }

    private static List<K8sCluster> Clusters
    {
        get
        {
            lock (Gate)
            {
                if (_clusters != null) return _clusters;
                _clusters = SafeFile.LoadProtected(FilePath, s => JsonSerializer.Deserialize<List<K8sCluster>>(s, Options), () => new List<K8sCluster>(), out var failed);
                LoadFailed = failed;
                return _clusters;
            }
        }
    }

    public static void Preload()
    {
        lock (Gate) _ = Clusters;
    }

    /// <summary>테스트용: 다음 읽기 때 파일에서 다시 읽는다</summary>
    internal static void ResetForTests()
    {
        lock (Gate) _clusters = null;
    }

    public static List<K8sCluster> All()
    {
        lock (Gate) return Clusters.ToList();
    }

    public static K8sCluster? Get(Guid? id)
    {
        if (id == null) return null;
        lock (Gate) return Clusters.FirstOrDefault(c => c.Id == id);
    }

    /// <summary>추가하거나 같은 Id 를 바꾼다. 저장에 실패하면 메모리 목록도 되돌리고 예외를 다시 던진다</summary>
    public static void Upsert(K8sCluster cluster)
    {
        lock (Gate)
        {
            var list = Clusters;
            var index = list.FindIndex(c => c.Id == cluster.Id);
            var old = index >= 0 ? list[index] : null;
            if (index >= 0) list[index] = cluster;
            else list.Add(cluster);
            try { Save(); }
            catch
            {
                if (index >= 0) list[index] = old!;
                else list.Remove(cluster);
                throw;
            }
        }
    }

    /// <summary>여러 개를 한 번에 바꾼다 (동기화·가져오기). 실패하면 되돌린다</summary>
    public static void ReplaceAll(List<K8sCluster> clusters)
    {
        lock (Gate)
        {
            var old = Clusters.ToList();
            _clusters = clusters;
            try { Save(); }
            catch { _clusters = old; throw; }
        }
    }

    public static void Remove(Guid id)
    {
        lock (Gate)
        {
            var list = Clusters;
            var index = list.FindIndex(c => c.Id == id);
            if (index < 0) return;
            var removed = list[index];
            list.RemoveAt(index);
            try { Save(); }
            catch { list.Insert(index, removed); throw; }
        }
    }

    private static void Save()
    {
        // 읽지 못한 원본의 사본도 만들지 못했으면 덮어쓰지 않는다 (VaultStore 와 같은 이유)
        if (!SafeFile.EnsureOriginalPreserved(FilePath))
            throw new IOException(L.T("k8s_clusters.json 을 읽지 못했고 원본 사본도 만들지 못해, 덮어쓰지 않도록 저장하지 않았습니다. 파일을 복구하거나 다른 곳으로 옮긴 뒤 Nubis 를 다시 시작하세요."));
        SafeFile.WriteProtected(FilePath, JsonSerializer.Serialize(Clusters, Options));
        LoadFailed = false;
    }
}
