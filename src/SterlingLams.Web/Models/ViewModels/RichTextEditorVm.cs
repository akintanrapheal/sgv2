namespace SterlingLams.Web.Models.ViewModels;

/// <summary>
/// Drives the shared <c>_RichTextEditor</c> partial. <see cref="Name"/> is the posted form field
/// name (the hidden textarea), <see cref="Id"/> a unique DOM id, <see cref="Html"/> the current
/// stored value, and <see cref="MinHeight"/> a Tailwind min-height class for the editing area.
/// </summary>
public record RichTextEditorVm(string Name, string Id, string? Html, string MinHeight = "min-h-[8rem]");
