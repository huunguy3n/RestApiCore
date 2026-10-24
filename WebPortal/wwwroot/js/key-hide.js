// Ẩn private_key khỏi màn hình sau N giây (không để lại trên màn hình bị bỏ quên)
(function () {
    const panel = document.getElementById("key-panel");
    const value = document.getElementById("key-value");
    if (!panel || !value) return;
    const seconds = parseInt(panel.dataset.hideAfter || "60", 10);
    setTimeout(() => {
        value.textContent = "•••••••• (đã ẩn, tải lại trang để xem lại)";
    }, seconds * 1000);
})();
