using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Nubis;

public partial class MainWindow
{
    // =================== MCP 승인 창 (요청 취소 반영) ===================

    /// <summary>
    /// ApprovalDialog.Ask + Claude 요청 취소 처리. UI 스레드에서 호출.
    /// Claude 에서 Esc 등으로 요청을 취소하면(notifications/cancelled) 열려 있는 이 승인 창을 거부로 닫고,
    /// 승인 직후라도 취소됐으면 실행하지 않는다 — 취소된 요청이 나중에 승인돼 명령·입력·전송이 실행되는 일을 막는다.
    /// 취소면 감사 로그를 남기고 McpToolException, 아니면 승인 여부를 돌려준다 (승인·거부 감사 로그는 호출한 쪽이 남긴다).
    /// 승인 창은 한 번에 하나만 띄운다 (_approvalGate): 다른 요청의 승인 창이 열려 있으면 그 창이 닫힐 때까지 비동기로 기다린다.
    /// 예전에는 두 번째 요청의 모달 창이 첫 창 위에 겹쳐 떠(중첩 메시지 루프), 첫 창을 승인해도 두 번째 창에 답할 때까지 실행되지 않았다.
    /// 기다리는 중에도 요청 취소를 반영한다.
    /// </summary>
    private async Task<bool> AskApprovalAsync(string tool, string title, string description, IReadOnlyList<string> targets, string command)
    {
        var ct = McpRequest.Token;
        if (ct.IsCancellationRequested) ThrowCancelled(tool, targets, command, false);
        try { await _approvalGate.WaitAsync(ct); }
        catch (OperationCanceledException) { ThrowCancelled(tool, targets, command, false); }
        try { return ShowApproval(tool, title, description, targets, command, ct); }
        finally { _approvalGate.Release(); }
    }

    /// <summary>승인 창을 한 번에 하나만 띄우기 위한 게이트 (UI 스레드에서만 사용)</summary>
    private readonly SemaphoreSlim _approvalGate = new(1, 1);

    private bool ShowApproval(string tool, string title, string description, IReadOnlyList<string> targets, string command, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) ThrowCancelled(tool, targets, command, false);

        // Ask 는 창을 만들자마자 Owner 를 지정하므로, 그 전에 열려 있던 승인 창과 비교해 이번 창을 찾는다.
        // Send 우선순위라 ShowDialog 의 메시지 루프가 돌자마자(다른 요청의 승인 창이 열리기 전에) 실행된다
        var before = OwnedWindows.OfType<ApprovalDialog>().ToHashSet();
        ApprovalDialog? mine = null;
        Dispatcher.InvokeAsync(() => mine = OwnedWindows.OfType<ApprovalDialog>().FirstOrDefault(w => !before.Contains(w)), DispatcherPriority.Send);
        using var reg = ct.CanBeCanceled
            ? ct.Register(() => Dispatcher.InvokeAsync(() => { try { mine?.Close(); } catch (InvalidOperationException) { } }))
            : default;

        var ok = ApprovalDialog.Ask(this, title, description, targets, command);
        if (ct.IsCancellationRequested) ThrowCancelled(tool, targets, command, ok);
        return ok;
    }

    private static void ThrowCancelled(string tool, IReadOnlyList<string> targets, string command, bool approved)
    {
        AuditLog.Write(tool, targets, command, approved ? "승인했으나 요청이 취소되어 실행 안 함" : "요청 취소");
        throw new McpToolException("Claude 쪽에서 요청이 취소되어 실행하지 않았습니다.");
    }

    /// <summary>
    /// 취소 토큰을 보는 스트림 래퍼 — SFTP 업로드·다운로드가 파일 하나를 읽고 쓰는 도중에도 요청 취소로 멈추게 한다.
    /// SSH.NET 의 UploadFile/DownloadFile 은 호출한 스레드에서 이 스트림을 읽고 쓰므로 예외가 그대로 호출자에게 온다.
    /// </summary>
    private sealed class CancellableStream(Stream inner, CancellationToken ct) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);

        public override int Read(byte[] buffer, int offset, int count)
        {
            ct.ThrowIfCancellationRequested();
            return inner.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ct.ThrowIfCancellationRequested();
            inner.Write(buffer, offset, count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
