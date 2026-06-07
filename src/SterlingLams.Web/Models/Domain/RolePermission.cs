namespace SterlingLams.Web.Models.Domain;

/// <summary>
/// Grants a role access to one admin section. One row per (role, section).
/// The "Admin" role is implicit full-access and has no rows here.
/// </summary>
public class RolePermission
{
    public int Id { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public string Section { get; set; } = string.Empty;
}
