using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Nubis;

// MCP 도구: SSH 탭의 로컬 포트 포워딩 (ssh -L) — 목록 · 이 탭에서만 임시 추가 · 닫기 (BuildTools 에서 등록)
// 보안 원칙:
// - 포워딩 추가는 이 PC 의 포트를 열어 서버 뒤로 들어가는 길을 만든다 — 항상 승인 창 + 감사 로그 (run_command 와 같은 실행 경로 규칙)
// - 바인드는 늘 127.0.0.1 (SshPortForwardSet). 호스트에 저장하는 규칙은 사용자가 호스트 편집에서만 바꾼다
// - 닫기는 노출을 줄이는 동작이라 승인 없이 감사 로그만
public partial class MainWindow
{
    private IEnumerable<McpTool> PortForwardTools()
    {
        yield return new McpTool("list_port_forwards",
            "SSH 탭의 로컬 포트 포워딩(ssh -L) 목록: 포워딩 id · 로컬 주소(127.0.0.1:포트) → 대상(SSH 서버에서 본 호스트:포트) · 이름 · 상태(open/waiting/off/failed) · " +
            "저장된 규칙인지 이 탭에서만 임시로 추가한 것인지 · 받은 연결 수 · 오류. session_id 생략 시 모든 SSH 탭. " +
            "DB 클라이언트나 브라우저는 open 상태의 로컬 주소로 접속하면 된다.",
            Schema(new
            {
                session_id = new { type = "integer", description = "이 탭만 (생략 시 모든 SSH 탭)" },
            }),
            a => OnUi(() =>
            {
                var tabs = IntArg(a, "session_id") is { } id ? _tabs.Where(t => t.Id == id).ToList() : _tabs.ToList();
                if (IntArg(a, "session_id") != null && tabs.Count == 0) throw new McpToolException("세션을 찾을 수 없습니다. list_sessions 로 확인하세요.");
                var ssh = tabs.Where(t => t.View is SshSessionView).ToList();
                if (ssh.Count == 0) return tabs.Count > 0 ? "SSH 탭이 아닙니다. 포트 포워딩은 SSH 탭에만 있습니다." : "열린 SSH 탭이 없습니다.";
                var sb = new StringBuilder("session_id | 호스트 | 포워딩 id | 로컬 → 대상 | 이름 | 상태 | 종류 | 연결 수 | 오류\n");
                var any = false;
                foreach (var t in ssh)
                {
                    var view = (SshSessionView)t.View;
                    foreach (var f in view.Forwards.Items)
                    {
                        any = true;
                        sb.AppendLine($"{t.Id} | {McpHelpers.VisibleControlChars(t.Title)} | {f.Id} | {f.Describe} | {McpHelpers.VisibleControlChars(f.Rule.Name ?? "-")} | " +
                                      $"{f.State.ToString().ToLowerInvariant()} | {(f.Temporary ? "임시(이 탭만)" : "저장된 규칙")} | {f.Connections} | {McpHelpers.VisibleControlChars(f.Error ?? "-")}");
                    }
                }
                return any ? sb.ToString()
                    : "SSH 탭에 포트 포워딩이 없습니다. 저장된 규칙은 사용자가 호스트 편집 → 포트 포워딩에서 추가하고, add_port_forward 로 이 탭에만 임시로 열 수 있습니다.";
            }));

        yield return new McpTool("add_port_forward",
            "SSH 탭에 로컬 포트 포워딩을 임시로 연다 (ssh -L local_port:target_host:target_port, 이 탭에서만 · 호스트에 저장 안 함). " +
            "탭의 SSH 연결을 그대로 쓰고(다시 로그인하지 않음) 이 PC 의 127.0.0.1 에만 연다. target_host 는 SSH 서버에서 본 주소 (생략 시 localhost = 서버 자신). " +
            "이 PC 의 포트를 여는 동작이라 사용자 승인을 받는다. 결과 상태는 list_port_forwards 로 확인.",
            Schema(new
            {
                session_id = new { type = "integer", description = "SSH 탭 (list_sessions)" },
                local_port = new { type = "integer", description = "이 PC 에서 열 포트 (1~65535)" },
                target_host = new { type = "string", description = "대상 호스트 (서버에서 본 주소, 기본 localhost)" },
                target_port = new { type = "integer", description = "대상 포트 (1~65535)" },
                name = new { type = "string", description = "표시 이름 (선택, 예: prod-db)" },
            }, "session_id", "local_port", "target_port"),
            a => OnUiAsync(async () =>
            {
                var tab = RequireSession(a, allowCurrent: false);
                if (tab.View is not SshSessionView) throw new McpToolException("SSH 탭이 아닙니다. 포트 포워딩은 SSH 탭에만 열 수 있습니다.");
                var host = Arg(a, "target_host");
                if (host.Length > 0 && McpHelpers.HasInvisibleChars(host)) throw new McpToolException("target_host 에 보이지 않는 문자가 있습니다.");
                var (rule, err) = PortForwards.Parse(IntArg(a, "local_port")?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    host, IntArg(a, "target_port")?.ToString(System.Globalization.CultureInfo.InvariantCulture), Arg(a, "name"));
                if (rule == null) throw new McpToolException("잘못된 인자: " + err);
                var target = SessionAuditTarget(tab);
                var what = PortForwards.Describe(rule) + (rule.Name != null ? $" ({rule.Name})" : "");
                var ok = await AskApprovalAsync("add_port_forward", L.T("Claude 가 포트 포워딩 열기를 요청합니다"),
                    L.F("이 PC 의 {0} 으로 들어오는 연결을 SSH 서버를 거쳐 {1} 로 보냅니다 (이 탭에서만, 탭을 닫으면 닫힘). 이 PC 의 다른 프로그램도 이 포트를 쓸 수 있습니다.",
                        PortForwards.LocalAddress(rule.LocalPort), PortForwards.TargetAddress(rule.TargetHost, rule.TargetPort)),
                    new[] { target }, what);
                if (!ok)
                {
                    AuditLog.Write("add_port_forward", new[] { target }, what, "거부");
                    throw new McpToolException("사용자가 거부했습니다.");
                }
                // 승인 창을 보는 사이 탭이 닫혔을 수 있다
                if (!_tabs.Contains(tab) || tab.View is not SshSessionView view)
                {
                    AuditLog.Write("add_port_forward", new[] { target }, what, "승인 후 취소 (탭이 닫힘)");
                    throw new McpToolException($"session {tab.Id} 은 기다리는 사이 닫혔습니다.");
                }
                var (f, addErr) = view.Forwards.AddTemporary(rule);
                if (f == null)
                {
                    AuditLog.Write("add_port_forward", new[] { target }, what, "승인 · 실패: " + addErr);
                    throw new McpToolException("포워딩을 추가하지 못했습니다: " + addErr);
                }
                var state = f.State switch
                {
                    ForwardState.Open => "열림",
                    ForwardState.Waiting => "대기 (탭이 연결되면 열림)",
                    ForwardState.Failed => "실패: " + f.Error,
                    _ => "꺼짐",
                };
                AuditLog.Write("add_port_forward", new[] { target }, what, "승인 · " + state);
                return $"포워딩 id {f.Id}: {f.Describe} — {state}";
            }));

        yield return new McpTool("close_port_forward",
            "SSH 탭의 포트 포워딩을 닫는다. 임시로 추가한 것은 목록에서 지우고, 저장된 규칙은 이 탭에서만 끈다 (호스트에 저장된 규칙은 그대로). " +
            "forward_id 는 list_port_forwards 의 포워딩 id.",
            Schema(new
            {
                session_id = new { type = "integer" },
                forward_id = new { type = "integer" },
            }, "session_id", "forward_id"),
            a => OnUi(() =>
            {
                var tab = RequireSession(a, allowCurrent: false);
                if (tab.View is not SshSessionView view) throw new McpToolException("SSH 탭이 아닙니다.");
                var f = IntArg(a, "forward_id") is { } fid ? view.Forwards.Find(fid) : null;
                if (f == null) throw new McpToolException("포워딩을 찾을 수 없습니다. list_port_forwards 로 확인하세요.");
                if (f.Temporary) view.Forwards.Remove(f);
                else if (view.Forwards.SetEnabled(f, false) is { } err) throw new McpToolException(err);
                AuditLog.Write("close_port_forward", new[] { SessionAuditTarget(tab) }, f.Describe, f.Temporary ? "임시 포워딩 지움" : "이 탭에서 끔");
                return $"포워딩 id {f.Id} ({f.Describe}) 를 {(f.Temporary ? "닫고 지웠습니다" : "이 탭에서 껐습니다 (호스트의 저장된 규칙은 그대로)")}.";
            }));
    }
}
