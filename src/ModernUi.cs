using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Hd820GameBridge {

 // 浅色现代主题的调色板与绘图工具。
 internal static class UiTheme {
  internal static readonly Color Window       = Color.FromArgb(245,247,251);
  internal static readonly Color Card         = Color.White;
  internal static readonly Color CardBorder   = Color.FromArgb(227,232,241);
  internal static readonly Color PrimaryText  = Color.FromArgb(27,36,48);
  internal static readonly Color SecondaryText= Color.FromArgb(94,110,131);
  internal static readonly Color MutedText    = Color.FromArgb(148,163,184);
  internal static readonly Color Accent       = Color.FromArgb(47,107,255);
  internal static readonly Color AccentLight  = Color.FromArgb(84,136,255);
  internal static readonly Color AccentDeep   = Color.FromArgb(38,85,224);
  internal static readonly Color Track        = Color.FromArgb(226,232,240);
  internal static readonly Color Border       = Color.FromArgb(213,220,232);
  internal static readonly Color DisabledBack = Color.FromArgb(243,245,249);
  internal static readonly Color Green        = Color.FromArgb(22,163,74);
  internal static readonly Color GreenBack    = Color.FromArgb(228,246,234);
  internal static readonly Color BlueBack     = Color.FromArgb(232,240,255);
  internal static readonly Color Amber        = Color.FromArgb(198,120,10);
  internal static readonly Color AmberBack    = Color.FromArgb(253,243,226);
  internal static readonly Color Red          = Color.FromArgb(203,43,43);
  internal static readonly Color RedBack      = Color.FromArgb(253,232,232);
  internal static readonly Color Gray         = Color.FromArgb(100,116,139);
  internal static readonly Color GrayBack     = Color.FromArgb(237,240,245);

  internal static GraphicsPath Rounded(Rectangle bounds,int radius) {
   if(bounds.Width<=1 || bounds.Height<=1){var flat=new GraphicsPath();if(bounds.Width>0 && bounds.Height>0)flat.AddRectangle(bounds);return flat;}
   if(radius<=0)radius=1;
   if(radius>bounds.Width/2)radius=bounds.Width/2;
   if(radius>bounds.Height/2)radius=bounds.Height/2;
   var path=new GraphicsPath();int d=radius*2;
   path.AddArc(bounds.X,bounds.Y,d,d,180,90);
   path.AddArc(bounds.Right-d,bounds.Y,d,d,270,90);
   path.AddArc(bounds.Right-d,bounds.Bottom-d,d,d,0,90);
   path.AddArc(bounds.X,bounds.Bottom-d,d,d,90,90);
   path.CloseFigure();
   return path;
  }
  internal static Color ParentColor(Control control) {
   return control!=null && control.Parent!=null?control.Parent.BackColor:Card;
  }
  internal static void ReplaceRegion(Control control,GraphicsPath path){var previous=control.Region;control.Region=new Region(path);if(previous!=null)previous.Dispose();}
  internal static void DrawTextCentered(Graphics g,string text,Font font,Color color,Rectangle bounds) {
   TextRenderer.DrawText(g,text==null?"":text,font,bounds,color,
    TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPadding);
  }
  internal static Color Blend(Color a,Color b,float t) {
   return Color.FromArgb((int)(a.R+(b.R-a.R)*t),(int)(a.G+(b.G-a.G)*t),(int)(a.B+(b.B-a.B)*t));
  }
 }

 // 圆角扁平按钮，带悬停/按压反馈与焦点环。
 internal sealed class ModernButton : Control {
  internal enum Kind { Primary, Default, Ghost, Danger }
  readonly Kind kind;
  bool hover,pressed;
  internal ModernButton(Kind kind) {
   this.kind=kind;
   SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.UserPaint|ControlStyles.Selectable|ControlStyles.SupportsTransparentBackColor,true);
   UpdateStyles();
   // WinForms already raises Click once for a normal mouse-up message.
   TabStop=true;Cursor=Cursors.Hand;
  }
  protected override void OnPaintBackground(PaintEventArgs e) {}
  protected override void OnMouseEnter(EventArgs e) { hover=true;Invalidate();base.OnMouseEnter(e); }
  protected override void OnMouseLeave(EventArgs e) { hover=false;pressed=false;Invalidate();base.OnMouseLeave(e); }
  protected override void OnMouseDown(MouseEventArgs e) { if(e.Button==MouseButtons.Left){Focus();pressed=true;Invalidate();}base.OnMouseDown(e); }
  protected override void OnMouseUp(MouseEventArgs e) {
   pressed=false;Invalidate();
   base.OnMouseUp(e);
  }
  protected override void OnKeyDown(KeyEventArgs e) {
   if(Enabled && (e.KeyCode==Keys.Enter||e.KeyCode==Keys.Space)){OnClick(EventArgs.Empty);e.Handled=true;e.SuppressKeyPress=true;return;}
   base.OnKeyDown(e);
  }
  protected override void OnPaint(PaintEventArgs e) {
   var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
   Color parent=UiTheme.ParentColor(this);
   g.Clear(parent);
   Rectangle rect=ClientRectangle;rect.Inflate(-1,-1);
   if(rect.Width<4||rect.Height<4)return;
   Color fill,border,text;
   if(!Enabled){fill=UiTheme.DisabledBack;border=UiTheme.Track;text=UiTheme.MutedText;}
   else if(kind==Kind.Primary){fill=UiTheme.Accent;border=UiTheme.Accent;text=Color.White;}
   else if(kind==Kind.Danger){fill=Color.FromArgb(254,242,242);border=Color.FromArgb(254,202,202);text=UiTheme.Red;}
   else{fill=kind==Kind.Ghost?parent:Color.White;border=UiTheme.Border;text=kind==Kind.Ghost?UiTheme.Gray:UiTheme.PrimaryText;}
   using(var path=UiTheme.Rounded(rect,9)){
    if(Enabled && kind==Kind.Primary){
     using(var brush=new LinearGradientBrush(rect,UiTheme.AccentLight,UiTheme.Accent,55f))g.FillPath(brush,path);
     if(hover){using(var overlay=new SolidBrush(Color.FromArgb(28,255,255,255)))g.FillPath(overlay,path);}
     if(pressed){using(var overlay=new SolidBrush(Color.FromArgb(30,0,0,0)))g.FillPath(overlay,path);}
    }else{
     Color f=fill;
     if(Enabled){
      if(hover)f=UiTheme.Blend(f,kind==Kind.Ghost?Color.White:Color.FromArgb(246,248,252),0.55f);
      if(pressed)f=UiTheme.Blend(f,Color.FromArgb(237,241,248),0.7f);
     }
     using(var brush=new SolidBrush(f))g.FillPath(brush,path);
    }
    Color b=border;
    if(Enabled && kind==Kind.Ghost && hover)b=UiTheme.Blend(UiTheme.Border,UiTheme.Accent,0.45f);
    using(var pen=new Pen(b,1f))g.DrawPath(pen,path);
   }
   if(Focused && Enabled){
    var ring=ClientRectangle;ring.Inflate(-4,-4);
    using(var pen=new Pen(Color.FromArgb(140,UiTheme.Accent),1.4f))
    using(var path=UiTheme.Rounded(ring,7))g.DrawPath(pen,path);
   }
   UiTheme.DrawTextCentered(g,Text,Font,text,ClientRectangle);
  }
 }

 // 拨动开关（Switch）。
 internal sealed class ModernSwitch : Control {
  bool state,hover;
  internal event EventHandler CheckedChanged;
  internal bool Checked {
   get{return state;}
   set{if(value==state)return;state=value;Invalidate();var handler=CheckedChanged;if(handler!=null)handler(this,EventArgs.Empty);}
  }
  internal ModernSwitch() {
   SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.UserPaint|ControlStyles.Selectable|ControlStyles.SupportsTransparentBackColor,true);
   UpdateStyles();
   TabStop=true;Cursor=Cursors.Hand;Size=new Size(46,28);
   Font=new Font("Microsoft YaHei UI",9.75f);
  }
  int PillHeight{get{return Math.Max(16,(int)Math.Round(FontHeight*1.18f));}}
  protected override void OnPaintBackground(PaintEventArgs e) {}
  protected override void OnMouseEnter(EventArgs e) { hover=true;Invalidate();base.OnMouseEnter(e); }
  protected override void OnMouseLeave(EventArgs e) { hover=false;Invalidate();base.OnMouseLeave(e); }
  protected override void OnMouseUp(MouseEventArgs e) {
   if(e.Button==MouseButtons.Left && ClientRectangle.Contains(e.Location))Checked=!Checked;
   base.OnMouseUp(e);
  }
  protected override void OnKeyDown(KeyEventArgs e) {
   if(e.KeyCode==Keys.Space){Checked=!Checked;e.Handled=true;return;}
   base.OnKeyDown(e);
  }
  protected override void OnPaint(PaintEventArgs e) {
   var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
   g.Clear(UiTheme.ParentColor(this));
   int h=PillHeight,w=(int)Math.Round(h*1.9f);
   var pill=new Rectangle((Width-w)/2,(Height-h)/2,w,h);
   if(pill.Width<12)return;
   using(var path=UiTheme.Rounded(pill,h/2)){
    Color fill;
    if(!Enabled)fill=UiTheme.Track;
    else if(state)fill=hover?UiTheme.AccentLight:UiTheme.Accent;
    else fill=hover?Color.FromArgb(184,196,214):Color.FromArgb(201,210,224);
    using(var brush=new SolidBrush(fill))g.FillPath(brush,path);
   }
   int d=h-6;if(d<8)d=8;
   int thumbX=state?pill.Right-d-3:pill.X+3;
   var thumb=new Rectangle(thumbX,pill.Y+3,d,d);
   using(var shadow=new SolidBrush(Color.FromArgb(28,15,23,42)))g.FillEllipse(shadow,thumb.X+1,thumb.Y+2,thumb.Width,thumb.Height);
   using(var brush=new SolidBrush(!Enabled?Color.FromArgb(248,250,252):Color.White))g.FillEllipse(brush,thumb);
   using(var pen=new Pen(!Enabled?Color.FromArgb(211,218,230):Color.FromArgb(224,230,240),1f))g.DrawEllipse(pen,thumb);
   if(Focused && Enabled){
    var ring=pill;ring.Inflate(3,3);
    using(var pen=new Pen(Color.FromArgb(140,UiTheme.Accent),1.4f))
    using(var path=UiTheme.Rounded(ring,(ring.Height)/2))g.DrawPath(pen,path);
   }
  }
 }

 // 自绘滑杆：圆点滑块、已填充轨道、拖拽数值气泡。
 internal sealed class ModernSlider : Control {
  int min,max,value;
  bool hover,drag;
  internal event EventHandler Scroll;
  internal int Value {
   get{return value;}
   set{int v=Math.Max(min,Math.Min(max,value));if(v!=this.value){this.value=v;Invalidate();}}
  }
  internal int Minimum { get{return min;} set{min=value;this.value=Math.Max(min,Math.Min(max,this.value));Invalidate();} }
  internal int Maximum { get{return max;} set{max=value;this.value=Math.Max(min,Math.Min(max,this.value));Invalidate();} }
  internal ModernSlider() {
   SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.UserPaint|ControlStyles.Selectable|ControlStyles.SupportsTransparentBackColor,true);
   UpdateStyles();
   TabStop=true;Cursor=Cursors.Hand;Size=new Size(240,36);
   Font=new Font("Microsoft YaHei UI",9.75f);
  }
  float ThumbRadius { get{return Math.Max(6f,FontHeight*0.40f);} }
  Rectangle TrackBounds {
   get{
    int r=(int)Math.Ceiling(ThumbRadius);
    int y=(int)Math.Round(Height*0.72f);
    return new Rectangle(r,y,Math.Max(4,Width-2*r),5);
   }
  }
  float Ratio { get{return max==min?0f:(value-min)/(float)(max-min);} }
  int ThumbCenterX { get{var t=TrackBounds;return t.X+(int)Math.Round(t.Width*Ratio);} }
  protected override void OnPaintBackground(PaintEventArgs e) {}
  protected override void OnMouseEnter(EventArgs e) { hover=true;Invalidate();base.OnMouseEnter(e); }
  protected override void OnMouseLeave(EventArgs e) { hover=false;Invalidate();base.OnMouseLeave(e); }
  protected override void OnMouseDown(MouseEventArgs e) {
   if(Enabled && e.Button==MouseButtons.Left){Focus();drag=true;Capture=true;SetFromPoint(e.Location);RaiseScroll();Invalidate();}
   base.OnMouseDown(e);
  }
  protected override void OnMouseMove(MouseEventArgs e) {
   hover=true;
   if(drag){SetFromPoint(e.Location);RaiseScroll();Invalidate();}
   base.OnMouseMove(e);
  }
  protected override void OnMouseUp(MouseEventArgs e) {
   if(e.Button==MouseButtons.Left && drag){drag=false;Capture=false;Invalidate();}
   base.OnMouseUp(e);
  }
  protected override void OnMouseCaptureChanged(EventArgs e){if(!Capture && drag){drag=false;Invalidate();}base.OnMouseCaptureChanged(e);}
  protected override void OnEnabledChanged(EventArgs e){if(!Enabled){drag=false;Capture=false;}Invalidate();base.OnEnabledChanged(e);}
  protected override void OnMouseWheel(MouseEventArgs e) {
   if(!Enabled||drag)return;
   Value=value+(e.Delta>0?1:-1);RaiseScroll();
   base.OnMouseWheel(e);
  }
  protected override void OnKeyDown(KeyEventArgs e) {
   if(!Enabled)return;
   if(e.KeyCode==Keys.Left){Value=value-1;RaiseScroll();e.Handled=true;return;}
   if(e.KeyCode==Keys.Right){Value=value+1;RaiseScroll();e.Handled=true;return;}
   if(e.KeyCode==Keys.Home){Value=min;RaiseScroll();e.Handled=true;return;}
   if(e.KeyCode==Keys.End){Value=max;RaiseScroll();e.Handled=true;return;}
   base.OnKeyDown(e);
  }
  void SetFromPoint(Point p) {
   var t=TrackBounds;
   float ratio=(p.X-t.X)/(float)Math.Max(1,t.Width);
   ratio=Math.Max(0f,Math.Min(1f,ratio));
   Value=min+(int)Math.Round((max-min)*ratio);
  }
  void RaiseScroll() {
   var handler=Scroll;if(handler!=null)handler(this,EventArgs.Empty);
  }
  protected override void OnPaint(PaintEventArgs e) {
   var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
   g.Clear(UiTheme.ParentColor(this));
   var t=TrackBounds;
   int cx=ThumbCenterX;
   float r=ThumbRadius;
   using(var back=new SolidBrush(!Enabled?Color.FromArgb(237,241,247):UiTheme.Track))
   using(var backPath=UiTheme.Rounded(new Rectangle(t.X,t.Y,t.Width,t.Height),t.Height/2))
    g.FillPath(back,backPath);
   if(cx>t.X){
    int fillW=cx-t.X;if(fillW>0){
     using(var brush=new SolidBrush(!Enabled?Color.FromArgb(201,210,224):(hover||drag?UiTheme.AccentLight:UiTheme.Accent)))
     using(var path=UiTheme.Rounded(new Rectangle(t.X,t.Y,fillW,t.Height),t.Height/2))
      g.FillPath(brush,path);
    }
   }
   if(hover||drag){
    using(var glow=new SolidBrush(Color.FromArgb(46,UiTheme.Accent)))g.FillEllipse(glow,cx-r-4,Height*0.72f-r-4+2,2*(r+4),2*(r+4));
   }
   if(Focused && Enabled){
    using(var ring=new Pen(Color.FromArgb(140,UiTheme.Accent),1.2f))g.DrawEllipse(ring,cx-r-5,Height*0.72f-r-5+2,2*(r+5),2*(r+5));
   }
   using(var shadow=new SolidBrush(Color.FromArgb(26,15,23,42)))g.FillEllipse(shadow,cx-r+1,Height*0.72f-r+3,2*r,2*r);
   using(var thumbBrush=new SolidBrush(!Enabled?Color.FromArgb(248,250,252):Color.White))g.FillEllipse(thumbBrush,cx-r,Height*0.72f-r+2,2*r,2*r);
   using(var pen=new Pen(!Enabled?Color.FromArgb(211,218,230):(drag?UiTheme.AccentDeep:UiTheme.Accent),2f))g.DrawEllipse(pen,cx-r,Height*0.72f-r+2,2*r,2*r);
   if(drag){
    string label=value.ToString()+"%";
    using(var labelFont=new Font(Font.FontFamily,8.5f,FontStyle.Bold)){
    var size=TextRenderer.MeasureText(label,labelFont);
    int w=size.Width+16,h=size.Height+8;
    int x=cx-w/2;x=Math.Max(0,Math.Min(Width-w,x));
    using(var path=UiTheme.Rounded(new Rectangle(x,2,w,h),h/2))
    using(var brush=new SolidBrush(UiTheme.Accent))g.FillPath(brush,path);
    TextRenderer.DrawText(g,label,labelFont,new Rectangle(x,2,w,h),Color.White,
     TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.NoPadding);
    }
   }
  }
 }

 // 下拉框弹出列表（自绘浅色条目）。
 internal sealed class ModernComboPopup : Form {
  readonly ModernCombo owner;
  readonly PopupList list=new PopupList();
  internal ModernComboPopup(ModernCombo owner,int width,int count) {
   this.owner=owner;
   FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;
   StartPosition=FormStartPosition.Manual;BackColor=UiTheme.Border;Padding=new Padding(1);
   MinimizeBox=false;MaximizeBox=false;ControlBox=false;
   int itemH=list.ItemHeight;
   int height=Math.Min(count*itemH+2,262);
   Size=new Size(width,height);
   list.Dock=DockStyle.Fill;
   list.Items.AddRange(owner.Items.ToArray());
   list.SelectedIndex=owner.SelectedIndex>=0 && owner.SelectedIndex<list.Items.Count?owner.SelectedIndex:-1;
   Controls.Add(list);
  }
  protected override void OnShown(EventArgs e) {
   base.OnShown(e);
   using(var path=UiTheme.Rounded(new Rectangle(0,0,Width,Height),10))UiTheme.ReplaceRegion(this,path);
   list.Focus();
  }
  protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e);Close(); }
  protected override void OnKeyDown(KeyEventArgs e) {
   if(e.KeyCode==Keys.Escape){Close();e.Handled=true;return;}
   base.OnKeyDown(e);
  }
  internal void Commit(int index) { owner.SelectIndex(index);Close(); }
  internal void Cancel() { Close(); }
  sealed class PopupList : ListBox {
   int hover=-1;
   internal PopupList() {
    DrawMode=DrawMode.OwnerDrawFixed;ItemHeight=30;BorderStyle=BorderStyle.None;
    BackColor=Color.White;ForeColor=UiTheme.PrimaryText;IntegralHeight=false;
   }
   protected override void OnDrawItem(DrawItemEventArgs e) {
    if(e.Index<0)return;
    var g=e.Graphics;
    bool hov=e.Index==hover;
    Color back=hov?Color.FromArgb(238,244,255):Color.White;
    Color text=hov?UiTheme.Accent:UiTheme.PrimaryText;
    using(var brush=new SolidBrush(back))g.FillRectangle(brush,e.Bounds);
    if(hov)using(var brush=new SolidBrush(UiTheme.Accent))g.FillRectangle(brush,e.Bounds.X,e.Bounds.Y,3,e.Bounds.Height);
    TextRenderer.DrawText(g,Items[e.Index].ToString(),Font,
     new Rectangle(e.Bounds.X+12,e.Bounds.Y,e.Bounds.Width-24,e.Bounds.Height),text,
     TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPadding);
   }
   protected override void OnMouseMove(MouseEventArgs e) {
    int i=IndexFromPoint(e.Location);
    if(i!=hover){hover=i;Invalidate();}
    base.OnMouseMove(e);
   }
   protected override void OnMouseLeave(EventArgs e) {
    if(hover!=-1){hover=-1;Invalidate();}
    base.OnMouseLeave(e);
   }
   protected override void OnMouseUp(MouseEventArgs e) {
    int i=IndexFromPoint(e.Location);
    if(e.Button==MouseButtons.Left && i>=0)((ModernComboPopup)FindForm()).Commit(i);
    base.OnMouseUp(e);
   }
   protected override void OnKeyDown(KeyEventArgs e) {
    if(e.KeyCode==Keys.Escape){((ModernComboPopup)FindForm()).Cancel();e.Handled=true;return;}
    if(e.KeyCode==Keys.Enter){int i=SelectedIndex;if(i>=0)((ModernComboPopup)FindForm()).Commit(i);e.Handled=true;return;}
    base.OnKeyDown(e);
   }
  }
 }

 // 现代下拉选择框：圆角外壳 + 自定义弹出列表。
 internal sealed class ModernCombo : Control {
  readonly List<object> items=new List<object>();
  int selected=-1;
  bool hover;
  ModernComboPopup popup;
  string placeholder="请选择…";
  internal event EventHandler SelectedIndexChanged;
  internal List<object> Items { get{return items;} }
  internal int SelectedIndex { get{return selected;} set{SelectIndex(value);} }
  internal object SelectedItem { get{return selected>=0&&selected<items.Count?items[selected]:null;} }
  internal string PlaceholderText { get{return placeholder;} set{placeholder=value==null?"":value;Invalidate();} }
  internal ModernCombo() {
   SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.UserPaint|ControlStyles.Selectable|ControlStyles.SupportsTransparentBackColor,true);
   UpdateStyles();
   TabStop=true;Cursor=Cursors.Hand;Size=new Size(200,34);
   Font=new Font("Microsoft YaHei UI",9.75f);
  }
  internal void SelectIndex(int index) {
   int clamped=items.Count==0?-1:Math.Max(-1,Math.Min(items.Count-1,index));
   if(clamped==selected)return;
   selected=clamped;Invalidate();
   var handler=SelectedIndexChanged;if(handler!=null)handler(this,EventArgs.Empty);
  }
  void OpenPopup() {
   if(!Enabled||items.Count==0||popup!=null)return;
   popup=new ModernComboPopup(this,Width,items.Count);
   popup.FormClosed+=delegate(object sender,FormClosedEventArgs e){var closed=(ModernComboPopup)sender;if(Object.ReferenceEquals(popup,closed))popup=null;closed.Dispose();};
   Point below=PointToScreen(new Point(0,Height+4));
   Screen screen=Screen.FromPoint(below);
   if(below.Y+popup.Height>screen.WorkingArea.Bottom)below.Y=PointToScreen(Point.Empty).Y-popup.Height-4;
   popup.Location=below;
   try{popup.Show(this.FindForm());}catch{popup.Show();}
  }
  protected override void OnPaintBackground(PaintEventArgs e) {}
  protected override void OnMouseEnter(EventArgs e) { hover=true;Invalidate();base.OnMouseEnter(e); }
  protected override void OnMouseLeave(EventArgs e) { hover=false;Invalidate();base.OnMouseLeave(e); }
  protected override void OnMouseDown(MouseEventArgs e) {
   if(e.Button==MouseButtons.Left){Focus();OpenPopup();}
   base.OnMouseDown(e);
  }
  protected override void OnKeyDown(KeyEventArgs e) {
   if(e.KeyCode==Keys.Enter||e.KeyCode==Keys.Space||(e.Alt&&(e.KeyCode==Keys.Down||e.KeyCode==Keys.Up))||e.KeyCode==Keys.F4){OpenPopup();e.Handled=true;return;}
   if(e.KeyCode==Keys.Up||e.KeyCode==Keys.Down){
    if(items.Count>0){
     int next=selected+(e.KeyCode==Keys.Up?-1:1);
     next=Math.Max(0,Math.Min(items.Count-1,next));
     SelectIndex(next);
    }
    e.Handled=true;return;
   }
   base.OnKeyDown(e);
  }
  protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e);Invalidate(); }
  protected override void OnPaint(PaintEventArgs e) {
   var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
   g.Clear(UiTheme.ParentColor(this));
   Rectangle rect=ClientRectangle;rect.Inflate(-1,-1);
   if(rect.Width<4||rect.Height<4)return;
   using(var path=UiTheme.Rounded(rect,9))
   using(var fill=new SolidBrush(Enabled?Color.White:UiTheme.DisabledBack))
    g.FillPath(fill,path);
   Color border=!Enabled?UiTheme.Track:((hover||Focused)?UiTheme.Accent:UiTheme.Border);
   using(var pen=new Pen(border,1f))using(var path=UiTheme.Rounded(rect,9))g.DrawPath(pen,path);
   string text=SelectedItem!=null?SelectedItem.ToString():placeholder;
   Color textColor=Enabled?(SelectedItem!=null?UiTheme.PrimaryText:UiTheme.MutedText):UiTheme.MutedText;
   TextRenderer.DrawText(g,text,Font,new Rectangle(12,0,Width-40,Height),textColor,
    TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPadding);
   int cy=Height/2;
   using(var pen=new Pen(Enabled?Color.FromArgb(130,140,160):UiTheme.MutedText,1.6f)){
    pen.StartCap=LineCap.Round;pen.EndCap=LineCap.Round;
    g.DrawLine(pen,Width-22,cy-3,Width-15,cy+4);
    g.DrawLine(pen,Width-15,cy+4,Width-8,cy-3);
   }
  }
  protected override void Dispose(bool disposing) {
   if(disposing&&popup!=null)try{popup.Close();}catch{}
   base.Dispose(disposing);
  }
 }

 // 圆角卡片容器：白底、描边、可选标题。
 internal sealed class CardPanel : Panel {
  string titleText="";
  internal string TitleText { get{return titleText;} set{titleText=value==null?"":value;Invalidate();} }
  internal CardPanel() {
   SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.UserPaint,true);
   UpdateStyles();
   BackColor=UiTheme.Card;Padding=new Padding(14);
  }
  protected override void OnResize(EventArgs e) {
   base.OnResize(e);
   if(Width>0&&Height>0)using(var path=UiTheme.Rounded(new Rectangle(0,0,Width,Height),12))UiTheme.ReplaceRegion(this,path);
  }
  protected override void OnPaint(PaintEventArgs e) {
   base.OnPaint(e);
   var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
   if(Width>2&&Height>2){
    using(var pen=new Pen(UiTheme.CardBorder,1f))
    using(var path=UiTheme.Rounded(new Rectangle(0,0,Width-1,Height-1),12))
     g.DrawPath(pen,path);
   }
   if(titleText.Length>0){
    using(var brush=new SolidBrush(UiTheme.Accent))
    using(var bar=UiTheme.Rounded(new Rectangle(14,13,4,15),2))
     g.FillPath(brush,bar);
    using(var titleFont=new Font(Font.FontFamily,10f,FontStyle.Bold))TextRenderer.DrawText(g,titleText,titleFont,
     new Rectangle(26,10,Math.Max(40,Width-52),24),UiTheme.PrimaryText,
     TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.Left|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPadding);
   }
  }
 }

 // 状态胶囊：彩色圆点 + 文本。
 internal sealed class StatusPill : Control {
  string text="";
  Color dot,bg,fg;
  internal StatusPill() {
   SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.UserPaint|ControlStyles.SupportsTransparentBackColor,true);
   UpdateStyles();
   TabStop=false;Height=26;
   Font=new Font("Microsoft YaHei UI",9f);
  }
  internal void SetState(string t,Color dotColor,Color backColor,Color textColor) {
   text=t==null?"":t;dot=dotColor;bg=backColor;fg=textColor;
   var size=TextRenderer.MeasureText(text,Font);
   Width=size.Width+46;if(Width<92)Width=92;
   Invalidate();
  }
  protected override void OnPaintBackground(PaintEventArgs e) {}
  protected override void OnPaint(PaintEventArgs e) {
   var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
   g.Clear(UiTheme.ParentColor(this));
   var rect=ClientRectangle;rect.Width--;rect.Height--;
   using(var path=UiTheme.Rounded(rect,rect.Height/2))
   using(var brush=new SolidBrush(bg))g.FillPath(brush,path);
   using(var brush=new SolidBrush(dot))g.FillEllipse(brush,14,(Height-8)/2,8,8);
   TextRenderer.DrawText(g,text,Font,new Rectangle(28,0,Math.Max(4,Width-40),Height),fg,
    TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.Left|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPadding);
  }
 }
}
