<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Nhật ký request API" e="API request log"%>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server">
  <link href="<%= ResolveUrl("~/Css/api-log.css") %>?v=3" rel="stylesheet" type="text/css" />
</asp:Content>

<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
  <div id="apilog" class="apilog" data-handler="<%= ResolveUrl("~/AppHandler/ApiLogView.ashx") %>">

    <section class="apilog-card apilog-filter" aria-label="Bộ lọc">
      <div class="apilog-filter__grid">
        <div class="apilog-field">
          <label for="apilogFrom">Từ ngày</label>
          <input id="apilogFrom" type="date" required="required" />
        </div>
        <div class="apilog-field">
          <label for="apilogTo">Đến ngày</label>
          <input id="apilogTo" type="date" required="required" aria-describedby="apilogDateError" />
        </div>
        <div class="apilog-field">
          <label for="apilogUser">Tài khoản API</label>
          <input id="apilogUser" type="text" maxlength="32" autocomplete="off" spellcheck="false" />
        </div>
        <div class="apilog-field apilog-field--wide">
          <label for="apilogPath">Đường dẫn (tiền tố)</label>
          <input id="apilogPath" type="text" maxlength="256" autocomplete="off" spellcheck="false" placeholder="/api/..." />
        </div>
        <div class="apilog-field apilog-field--narrow">
          <label for="apilogStatusCode">HTTP status</label>
          <input id="apilogStatusCode" type="text" maxlength="3" autocomplete="off" spellcheck="false" placeholder="vd 500, 4xx" />
        </div>
        <div class="apilog-field">
          <label for="apilogResult">Mã kết quả</label>
          <input id="apilogResult" type="text" maxlength="65" autocomplete="off" spellcheck="false" placeholder="vd SUCCESS, !SUCCESS" />
        </div>
        <div class="apilog-field apilog-field--wide">
          <label for="apilogReqId">Request ID</label>
          <input id="apilogReqId" type="text" maxlength="36" autocomplete="off" spellcheck="false" placeholder="GUID, bỏ qua khoảng ngày khi nhập" />
        </div>
        <div class="apilog-filter__actions">
          <button type="button" id="apilogSearch" class="apilog-btn apilog-btn--primary" data-icon="search">Tìm kiếm</button>
          <button type="button" id="apilogClear" class="apilog-btn apilog-btn--ghost" data-icon="x">Xóa bộ lọc</button>
          <button type="button" id="apilogRefresh" class="apilog-btn apilog-btn--secondary" data-icon="refresh" title="Tải lại trang hiện tại với bộ lọc đang chọn">Làm mới</button>
          <span id="apilogLoadedAt" class="apilog-filter__loaded apilog-muted" aria-live="polite"></span>
        </div>
      </div>
      <p id="apilogDateError" class="apilog-filter__error" role="alert"></p>
    </section>

    <div id="apilogMain" class="apilog-main">
      <section class="apilog-card apilog-list" aria-label="Danh sách request">
        <div class="apilog-list__head">
          <div id="apilogFacets" class="apilog-facets" role="group" aria-label="Lọc nhanh theo kết quả"></div>
          <div class="apilog-list__meta">
            <span id="apilogNotice" class="apilog-muted"></span>
            <span id="apilogAvg" class="apilog-muted"></span>
            <span id="apilogCount" class="apilog-list__count" aria-live="polite"></span>
          </div>
        </div>
        <div id="apilogScroll" class="apilog-list__scroll">
          <table class="apilog-table">
            <thead>
              <tr>
                <th scope="col">Thời gian</th>
                <th scope="col">Tài khoản API</th>
                <th scope="col">Method</th>
                <th scope="col" class="apilog-col-path">Đường dẫn</th>
                <th scope="col">HTTP</th>
                <th scope="col">Mã kết quả</th>
                <th scope="col" class="apilog-num">Thời lượng</th>
                <th scope="col"><span class="apilog-sr">Thao tác</span></th>
              </tr>
            </thead>
            <tbody id="apilogRows"></tbody>
          </table>
          <div id="apilogState" class="apilog-state" hidden="hidden"></div>
        </div>
        <div class="apilog-pager">
          <span id="apilogRange" class="apilog-muted"></span>
          <div class="apilog-pager__nav" id="apilogPages" role="navigation" aria-label="Phân trang"></div>
          <label class="apilog-pager__size">Số dòng
            <select id="apilogPageSize">
              <option value="25">25</option>
              <option value="50">50</option>
              <option value="100">100</option>
            </select>
          </label>
        </div>
      </section>

      <div id="apilogResizer" class="apilog-resizer" role="separator" aria-orientation="vertical" aria-label="Kéo để đổi độ rộng panel chi tiết" tabindex="0"></div>
      <aside id="apilogDetail" class="apilog-card apilog-detail" aria-label="Chi tiết request" aria-live="polite"></aside>
      <div id="apilogBackdrop" class="apilog-backdrop"></div>
    </div>
  </div>

  <script src="<%= ResolveUrl("~/ClientScript/api-log.js") %>?v=3" type="text/javascript"></script>
</asp:Content>
