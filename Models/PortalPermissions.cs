namespace Models
{
    /// <summary>
    /// Quyền chi tiết của portal (capability). Nguồn sự thật cho vai trò → quyền là bảng restApiPortalRolePermission,
    /// giá trị không có trong All bị bỏ qua (deny). Không liên quan userinfo2.admin của FBO
    /// </summary>
    public static class PortalPermissions
    {
        public const string UsersRead = "users.read";
        public const string UsersCreate = "users.create";
        public const string UsersUpdate = "users.update";
        public const string UsersDeactivate = "users.deactivate";
        public const string UsersDelete = "users.delete";
        public const string UsersRolesManage = "users.roles.manage";
        public const string FormsRead = "forms.read";
        public const string FormsEdit = "forms.edit";
        public const string FormsPublish = "forms.publish";
        public const string FormsPermissionsManage = "forms.permissions.manage";
        public const string ControllersRead = "controllers.read";
        public const string ControllersCreate = "controllers.create";
        public const string ControllersUpdate = "controllers.update";
        public const string ControllersPublish = "controllers.publish";
        public const string CliInspect = "cli.inspect";
        public const string CliValidate = "cli.validate";
        public const string CliIssueKey = "cli.issue-key";
        public const string CliRotateKey = "cli.rotate-key";
        public const string CliRekeyPasswords = "cli.rekey-passwords";
        public const string AuditRead = "audit.read";
        public const string LogsRead = "logs.read";
        public const string LogsBodyRead = "logs.body.read";
        public const string SettingsManage = "settings.manage";

        public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        {
            UsersRead, UsersCreate, UsersUpdate, UsersDeactivate, UsersDelete, UsersRolesManage,
            FormsRead, FormsEdit, FormsPublish, FormsPermissionsManage,
            ControllersRead, ControllersCreate, ControllersUpdate, ControllersPublish,
            CliInspect, CliValidate, CliIssueKey, CliRotateKey, CliRekeyPasswords,
            AuditRead, LogsRead, LogsBodyRead, SettingsManage
        };
    }
}
