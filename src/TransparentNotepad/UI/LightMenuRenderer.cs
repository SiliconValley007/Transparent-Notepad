using System.Drawing.Drawing2D;
namespace TransparentNotepad.UI;

internal class CustomLightMenuRenderer : ToolStripProfessionalRenderer
{
    public CustomLightMenuRenderer() : base(new LightColors()) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        Rectangle rc = new Rectangle(Point.Empty, e.Item.Size);
        Color bkColor = e.Item.Selected ? Color.FromArgb(225, 225, 230) : Color.FromArgb(245, 245, 245);
        using SolidBrush brush = new SolidBrush(bkColor);
        e.Graphics.FillRectangle(brush, rc);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = Color.Black;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown)
        {
            using Pen borderPen = new Pen(Color.FromArgb(200, 200, 205));
            e.Graphics.DrawRectangle(borderPen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using Pen p = new Pen(Color.FromArgb(210, 210, 215));
        int y = e.Item.ContentRectangle.Height / 2;
        e.Graphics.DrawLine(p, 4, y, e.Item.Width - 4, y);
    }
}

internal class LightColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Color.FromArgb(245, 245, 245);
    public override Color ImageMarginGradientBegin => Color.FromArgb(245, 245, 245);
    public override Color ImageMarginGradientMiddle => Color.FromArgb(245, 245, 245);
    public override Color ImageMarginGradientEnd => Color.FromArgb(245, 245, 245);
    public override Color MenuBorder => Color.FromArgb(200, 200, 205);
    public override Color MenuItemPressedGradientBegin => Color.FromArgb(230, 230, 235);
    public override Color MenuItemPressedGradientEnd => Color.FromArgb(230, 230, 235);
    public override Color MenuItemSelected => Color.FromArgb(225, 225, 230);
    public override Color MenuItemSelectedGradientBegin => Color.FromArgb(225, 225, 230);
    public override Color MenuItemSelectedGradientEnd => Color.FromArgb(225, 225, 230);
}
