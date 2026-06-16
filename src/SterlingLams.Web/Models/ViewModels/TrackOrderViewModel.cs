using System.ComponentModel.DataAnnotations;
using SterlingLams.Web.Models.Domain;

namespace SterlingLams.Web.Models.ViewModels;

public class TrackOrderViewModel
{
    [Required(ErrorMessage = "Enter your order number.")]
    [Display(Name = "Order number")]
    public string OrderNumber { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter the email used on the order.")]
    [EmailAddress(ErrorMessage = "Enter a valid email address.")]
    public string Email { get; set; } = string.Empty;

    /// <summary>Populated on a successful lookup.</summary>
    public Order? Order { get; set; }
    public bool Searched { get; set; }
}
