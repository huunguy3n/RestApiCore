// Phiên đang mở hỏi mỗi 30 giây: có ai đăng nhập đúng mật khẩu ở nơi khác đang chờ thay phiên này không.
// Nếu có thì hiện cảnh báo; khi phiên bị thay, request kế tiếp sẽ đưa về trang đăng nhập kèm lý do.
(function () {
    const script = document.currentScript;
    const url = script && script.dataset.noticeUrl;
    const box = document.getElementById("session-notice");
    if (!url || !box) return;

    async function poll() {
        try {
            const response = await fetch(url, { credentials: "same-origin", cache: "no-store" });
            if (!response.ok) return;
            const data = await response.json();
            if (data.authenticated === false) {
                window.location.reload();
                return;
            }
            if (data.pending) {
                box.textContent = `Cảnh báo: tài khoản của bạn vừa được đăng nhập đúng mật khẩu từ IP ${data.ip || "?"} lúc ${data.at}. ` +
                    "Nếu người đó xác nhận, phiên này sẽ bị kết thúc. Nếu không phải bạn, hãy đổi mật khẩu ngay.";
                box.hidden = false;
            } else {
                box.hidden = true;
            }
        } catch {
            // Mất mạng tạm thời: thử lại ở lượt sau
        }
    }

    poll();
    setInterval(poll, 30000);
})();
