// Chọn dự án QLDA thì điền sẵn folder Api gợi ý (thư mục restApi cạnh program); người dùng vẫn sửa được trước khi lưu
document.querySelectorAll("select[data-folder-target]").forEach(select => {
    const target = document.getElementById(select.dataset.folderTarget);
    select.addEventListener("change", () => {
        const option = select.selectedOptions[0];
        if (target && option && option.dataset.folder && !target.dataset.touched) target.value = option.dataset.folder;
    });
    if (target) target.addEventListener("input", () => { target.dataset.touched = "1"; });
});
