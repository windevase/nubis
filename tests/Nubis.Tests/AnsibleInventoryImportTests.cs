using System.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

public class AnsibleInventoryImportTests
{
    private static ImportedHost Host(ImportResult r, string name) => Assert.Single(r.Hosts, h => h.Name == name);

    [Fact]
    public void INI_그룹_하위그룹_변수_우선순위()
    {
        var r = AnsibleInventoryImport.Parse("""
            # 주석
            [all:vars]
            ansible_user=alluser
            ansible_port=2200

            [prod:children]
            web

            [prod:vars]
            ansible_user=produser
            ansible_port=2300

            [web]
            web1 ansible_host=10.0.0.11
            web2 ansible_host=10.0.0.12 ansible_user=hostuser   # 인라인 주석

            [web:vars]
            ansible_port=2400

            [db]
            db1 ansible_host="10.0.0.21"
            """);

        Assert.False(AnsibleInventoryImport.LooksLikeYaml("[web]\nweb1"));
        var web1 = Host(r, "web1");
        Assert.Equal("10.0.0.11", web1.Address);
        Assert.Equal("produser", web1.Username);   // 그룹 변수 > all 변수
        Assert.Equal(2400, web1.Port);             // 하위 그룹 > 상위 그룹
        Assert.Equal("prod / web", web1.Group);
        Assert.Equal(Protocol.Ssh, web1.Protocol);

        var web2 = Host(r, "web2");
        Assert.Equal("hostuser", web2.Username);   // 호스트 변수 > 그룹 변수

        var db1 = Host(r, "db1");
        Assert.Equal("10.0.0.21", db1.Address);
        Assert.Equal("alluser", db1.Username);
        Assert.Equal(2200, db1.Port);
        Assert.Equal("db", db1.Group);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void INI_범위_확장_및_포트_축약()
    {
        var r = AnsibleInventoryImport.Parse("""
            [web]
            web[01:03].example.com
            node-[a:c]:2222
            bastion.example.com:2200
            plain
            """);

        Assert.Equal(new[] { "web01.example.com", "web02.example.com", "web03.example.com" },
            r.Hosts.Where(h => h.Name.StartsWith("web")).Select(h => h.Name).ToArray());
        Assert.Equal(new[] { "node-a", "node-b", "node-c" }, r.Hosts.Where(h => h.Name.StartsWith("node-")).Select(h => h.Name).ToArray());
        Assert.All(r.Hosts.Where(h => h.Name.StartsWith("node-")), h => Assert.Equal(2222, h.Port));
        var b = Host(r, "bastion.example.com");
        Assert.Equal("bastion.example.com", b.Address);
        Assert.Equal(2200, b.Port);
        Assert.Null(Host(r, "plain").Port);
    }

    [Fact]
    public void 범위가_한도를_넘으면_잘라내고_경고()
    {
        var r = AnsibleInventoryImport.Parse("[big]\nh[1:5000]\n");
        Assert.Equal(AnsibleInventoryImport.MaxHosts, r.Hosts.Count);
        Assert.Contains(r.Warnings, w => w.Contains(AnsibleInventoryImport.MaxHosts.ToString()));
    }

    [Fact]
    public void 그룹_밖의_호스트는_ungrouped()
    {
        var r = AnsibleInventoryImport.Parse("lonely ansible_host=192.0.2.1\n[web]\nweb1\n");
        Assert.Equal("ungrouped", Host(r, "lonely").Group);
        Assert.Equal("web", Host(r, "web1").Group);
    }

    [Fact]
    public void 비밀번호는_가져오지_않고_경고는_한_번()
    {
        var r = AnsibleInventoryImport.Parse("""
            [web]
            web1 ansible_password=secret1 ansible_user=u
            web2 ansible_ssh_pass=secret2 ansible_become_pass=secret3
            [web:vars]
            ansible_become_password=secret4
            """);

        Assert.Equal(2, r.Hosts.Count);
        Assert.Single(r.Warnings, w => w.Contains("ansible_password"));
        foreach (var h in r.Hosts)
            Assert.DoesNotContain("secret", string.Join("|", h.Name, h.Address, h.Username, h.Note, h.JumpHost, h.Group));
    }

    [Fact]
    public void WinRM_은_RDP_로_local_은_건너뜀()
    {
        var r = AnsibleInventoryImport.Parse("""
            [windows]
            win1 ansible_host=10.0.1.5 ansible_port=5986 ansible_user=Administrator
            [windows:vars]
            ansible_connection=winrm
            [ctl]
            localhost ansible_connection=local
            """);

        var win = Assert.Single(r.Hosts);
        Assert.Equal("win1", win.Name);
        Assert.Equal(Protocol.Rdp, win.Protocol);
        Assert.Null(win.Port); // WinRM 포트는 RDP 포트로 쓰지 않는다
        Assert.Equal("Administrator", win.Username);
        Assert.Contains("WinRM", win.Note);
        Assert.Equal("Windows", win.Os);
        Assert.Single(r.Warnings); // local 건너뜀 경고
    }

    [Fact]
    public void ProxyJump_인자에서_점프서버()
    {
        var r = AnsibleInventoryImport.Parse("""
            [bastions]
            bastion ansible_host=203.0.113.10
            [app]
            app1 ansible_ssh_common_args='-o ProxyJump=bastion'
            app2 ansible_ssh_common_args="-J ops@jump.example.com:2222"
            app3 ansible_ssh_common_args='-o ProxyCommand="ssh -W %h:%p -q -p 2022 ops@gw.example.com"'
            app4 ansible_ssh_common_args='-o ProxyJump=203.0.113.10'
            """);

        Assert.Equal("bastion", Host(r, "app1").JumpHost);
        Assert.Equal("ops@jump.example.com:2222", Host(r, "app2").JumpHost);
        Assert.Equal("ops@gw.example.com:2022", Host(r, "app3").JumpHost);
        Assert.Equal("bastion", Host(r, "app4").JumpHost); // 주소로 가리켜도 인벤토리 항목 이름으로
        Assert.Null(Host(r, "bastion").JumpHost);
    }

    [Fact]
    public void 여러_그룹의_호스트는_하나로_가장_구체적인_그룹()
    {
        var r = AnsibleInventoryImport.Parse("""
            [monitoring]
            shared1
            [prod:children]
            web
            [web]
            shared1 ansible_host=10.0.0.50
            [prod]
            shared1
            """);

        var h = Host(r, "shared1");
        Assert.Equal("prod / web", h.Group);           // 가장 깊은 그룹
        Assert.Equal("10.0.0.50", h.Address);
        Assert.Contains("monitoring", h.Note);
        Assert.DoesNotContain("prod", h.Note);         // 선택된 그룹의 상위 그룹은 Note 에 다시 쓰지 않는다
    }

    [Fact]
    public void YAML_중첩_children_과_변수()
    {
        var text = """
            all:
              vars:
                ansible_user: deploy
              hosts:
                solo:
                  ansible_host: 192.0.2.9
              children:
                prod:
                  vars:
                    ansible_port: 2201
                  children:
                    web:
                      hosts:
                        web[1:2].example.com:
                        special:
                          ansible_host: 10.1.1.1
                          ansible_port: 2222
                          ansible_user: admin
                          ansible_password: !vault |
                            $ANSIBLE_VAULT;1.1;AES256
                            6162
                    windows:
                      vars:
                        ansible_connection: winrm
                      hosts:
                        win1:
                local:
                  hosts:
                    localhost:
                      ansible_connection: local
            """;
        Assert.True(AnsibleInventoryImport.LooksLikeYaml(text));
        var r = AnsibleInventoryImport.Parse(text);

        Assert.Equal("ungrouped", Host(r, "solo").Group);
        Assert.Equal("192.0.2.9", Host(r, "solo").Address);

        var w1 = Host(r, "web1.example.com");
        Assert.Equal("prod / web", w1.Group);
        Assert.Equal("deploy", w1.Username);
        Assert.Equal(2201, w1.Port);
        Assert.Equal("web1.example.com", w1.Address);
        Assert.NotNull(Host(r, "web2.example.com"));

        var sp = Host(r, "special");
        Assert.Equal("10.1.1.1", sp.Address);
        Assert.Equal(2222, sp.Port);
        Assert.Equal("admin", sp.Username);

        var win = Host(r, "win1");
        Assert.Equal(Protocol.Rdp, win.Protocol);
        Assert.Equal("prod / windows", win.Group);

        Assert.DoesNotContain(r.Hosts, h => h.Name == "localhost");
        Assert.Contains(r.Warnings, w => w.Contains("ansible_password"));
    }

    [Fact]
    public void Jinja_템플릿_값은_건너뛰고_경고()
    {
        var r = AnsibleInventoryImport.Parse("""
            all:
              vars:
                ansible_user: base
              children:
                web:
                  hosts:
                    web1:
                      ansible_host: "{{ inventory_hostname }}.internal"
                      ansible_user: "{{ lookup('env', 'USER') }}"
                    web2:
                      ansible_host: "{{ inventory_hostname }}.internal"
            """);

        var w1 = Host(r, "web1");
        Assert.Equal("web1", w1.Address);  // 템플릿은 해석하지 않고 이름으로
        Assert.Null(w1.Username);          // 이긴 값이 템플릿이면 비운다
        Assert.Equal("web2", Host(r, "web2").Address);
        var warn = Assert.Single(r.Warnings);
        Assert.Contains("ansible_host", warn);
        Assert.Contains("ansible_user", warn);
    }

    [Fact]
    public void 잘못된_YAML_은_경고만()
    {
        var r = AnsibleInventoryImport.Parse("all:\n  hosts:\n    a: [unclosed\n");
        Assert.Empty(r.Hosts);
        Assert.Single(r.Warnings);
    }
}
