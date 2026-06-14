

# ===== fontstashsharp =====

I now have complete, source-verified data. The MonoGame `SpriteBatch.DrawString(...)` extensions mirror these `DrawText` overloads exactly (the MonoGame package provides `IFontStashRenderer` wrappers and `DrawString` extension methods with the identical parameter order). Here are the build-ready reference notes.

---

# FontStashSharp - MonoGame .NET 8 DesktopGL reference

## 1. NuGet package

```xml
<PackageReference Include="FontStashSharp.MonoGame" Version="1.5.4" />
```
```
dotnet add package FontStashSharp.MonoGame --version 1.5.4
```
- Package id: `FontStashSharp.MonoGame` (latest stable 1.5.4; 1.3.10 also widely used). Pulls in the core `FontStashSharp` + MonoGame `SpriteBatch.DrawString` extensions.
- Sibling packages (do NOT use for MonoGame DesktopGL): `FontStashSharp.FNA`, `FontStashSharp.Stride`, `FontStashSharp.Kni`, `FontStashSharp.XNA`.
- Namespace: `using FontStashSharp;`

## 2. Create FontSystem, add TTF, get font

```csharp
using FontStashSharp;

FontSystem _fontSystem;

// in LoadContent()
_fontSystem = new FontSystem();                                  // or new FontSystem(FontSystemSettings settings)
_fontSystem.AddFont(File.ReadAllBytes("Fonts/DroidSans.ttf"));   // byte[] overload
// Stream overload also exists: _fontSystem.AddFont(stream);
// Add multiple fonts as fallbacks (CJK, emoji) - glyph lookup falls through in add-order.

DynamicSpriteFont font18 = _fontSystem.GetFont(18);              // GetFont(float fontSize) -> DynamicSpriteFont
```
Verified signatures (src/FontStashSharp/FontSystem.cs):
```csharp
public FontSystem()
public FontSystem(FontSystemSettings settings)
public void AddFont(byte[] data)
public void AddFont(Stream stream)
public DynamicSpriteFont GetFont(float fontSize)
public void Reset()
```
`DynamicSpriteFont : SpriteFontBase`. Calling `GetFont` at any float size on one FontSystem reuses the same texture atlas - create one FontSystem per typeface, not per size.

## 3. Draw text with SpriteBatch

Simplest (MonoGame `DrawString` extension; mirrors core `DrawText`):
```csharp
_spriteBatch.Begin();
_spriteBatch.DrawString(font18, "Hello\nWorld", new Vector2(0, 0), Color.White);
_spriteBatch.End();
```
Full parameter order (scale/rotation/origin) - extension signature matches the core `SpriteFontBase.DrawText` overload exactly:
```csharp
// MonoGame XNA Color, Vector2
_spriteBatch.DrawString(
    font,
    "text",
    position,                 // Vector2
    Color.White,              // or Color[] glyphColors for per-glyph color
    rotation: 0f,             // float, radians
    origin: Vector2.Zero,     // Vector2
    scale: new Vector2(1f),   // Vector2? (null => 1,1)
    layerDepth: 0f,
    characterSpacing: 0f,
    lineSpacing: 0f,
    textStyle: TextStyle.None,
    effect: FontSystemEffect.None,
    effectAmount: 0);
```
Verified core overloads (src/FontStashSharp/SpriteFontBase.IFontStashRenderer.cs) - the `DrawString` SpriteBatch extension wraps these (SpriteBatch substitutes for `IFontStashRenderer`):
```csharp
public float DrawText(IFontStashRenderer renderer, string text, Vector2 position, Color color,
    float rotation = 0, Vector2 origin = default(Vector2), Vector2? scale = null,
    float layerDepth = 0.0f, float characterSpacing = 0.0f, float lineSpacing = 0.0f,
    TextStyle textStyle = TextStyle.None, FontSystemEffect effect = FontSystemEffect.None, int effectAmount = 0)
// overloads: Color[] colors; StringSegment; StringBuilder
```
- Order is `position, color, rotation, origin, scale, layerDepth, ...`. Note `rotation` comes BEFORE `origin`/`scale` (opposite of MonoGame's native `SpriteFont.DrawString`). Use named args to avoid mistakes.
- `scale` is `Vector2?` - pass `null` for 1:1.
- Glow/outline via `effect:`/`effectAmount:`:
```csharp
_spriteBatch.DrawString(font, "text", pos, Color.Yellow,
    effect: FontSystemEffect.Blurry, effectAmount: 1);   // also FontSystemEffect.Stroked
```
- You can also call directly on the font: `font.DrawText(_spriteBatch, text, pos, Color.White, scale: scale);`

## 4. Measure + crisp scaling

MeasureString (src/FontStashSharp/SpriteFontBase.cs):
```csharp
public Vector2 MeasureString(string text, Vector2? scale = null, float characterSpacing = 0.0f,
    float lineSpacing = 0.0f, FontSystemEffect effect = FontSystemEffect.None, int effectAmount = 0)
// StringBuilder overload identical.
// Also: Bounds TextBounds(string text, Vector2 position, Vector2? scale = null, ...)
```
```csharp
Vector2 size = font.MeasureString("text");
Vector2 size2 = font.MeasureString("text", new Vector2(2f, 2f)); // measured at scale
```

Crisp text at varying sizes - set BEFORE creating any FontSystem, via global `FontSystemDefaults`:
```csharp
FontSystemDefaults.FontResolutionFactor = 2.0f;  // render glyphs to atlas at 2x => crisp when scaled up
FontSystemDefaults.KernelWidth  = 2;             // stb_truetype prefilter (anti-alias smoothing)
FontSystemDefaults.KernelHeight = 2;
```
Or per-system via `FontSystemSettings` (src/FontStashSharp/FontSystemSettings.cs, defaults shown):
```csharp
var settings = new FontSystemSettings
{
    TextureWidth         = 1024,   // atlas page size; raise to 2048 if you use many sizes/glyphs
    TextureHeight        = 1024,
    FontResolutionFactor = 2.0f,   // default 1.0f
    KernelWidth          = 2,      // default 0
    KernelHeight         = 2,      // default 0
    PremultiplyAlpha     = true,   // default true (atlas is premultiplied)
};
var fontSystem = new FontSystem(settings);
```
- Best practice: instead of scaling one size up, call `GetFont(actualPixelSize)` per size - glyphs are then rasterized natively crisp. `FontResolutionFactor=2` + `KernelWidth/Height=2` is the recommended combo for smooth scaling.
- Tradeoff: higher `FontResolutionFactor` consumes atlas space ~quadratically; the 1024² atlas fills faster - bump `TextureWidth/Height` to 2048 if you hit atlas-full.
- `PremultiplyAlpha = true` means draw with `_spriteBatch.Begin(blendState: BlendState.AlphaBlend)` (premultiplied). If you instead see dark halos, you have a premultiply mismatch - keep Begin's blend state premultiplied, or set `PremultiplyAlpha = false` and use `BlendState.NonPremultiplied`.

## 5. Linux / .NET 8 gotchas

- Pure managed (StbTrueTypeSharp); no native libfreetype dependency for the default rasterizer, so DesktopGL on Linux works out of the box. The only native dep is MonoGame's own SDL2/OpenAL (provided by `MonoGame.Framework.DesktopGL`).
- Load `.ttf` at runtime with `File.ReadAllBytes` / `Stream` - do NOT route through the MonoGame content pipeline (no MGCB needed for fonts). Ship `.ttf` as files: mark with `<Content Include="Fonts\*.ttf"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></Content>` (or `<None ... CopyToOutputDirectory>`). On case-sensitive Linux filesystems, match the file path casing exactly.
- Set `FontSystemDefaults.*` once at startup before constructing any `FontSystem`; changing defaults afterward won't retro-apply to existing systems.
- One `FontSystem`/atlas is bound to the `GraphicsDevice`; recreate fonts after a device reset. `font.DrawText`/`DrawString` must be called between `SpriteBatch.Begin()`/`End()`.
- If using HarfBuzz text shaping (`FontStashSharp.Rich`/HarfBuzz sample), that DOES add a native `libHarfBuzzSharp` dependency - not needed for plain Latin/most rendering; skip it on Linux unless you require complex-script shaping.

Sources:
- [github.com/FontStashSharp/FontStashSharp](https://github.com/FontStashSharp/FontStashSharp)
- [Wiki: Using FontStashSharp in MonoGame or FNA](https://github.com/FontStashSharp/FontStashSharp/wiki/Using-FontStashSharp-in-MonoGame-or-FNA)
- [Wiki: Making Fonts Sharper And Better At Scaling](https://github.com/FontStashSharp/FontStashSharp/wiki/Making-Fonts-Sharper-And-Better-At-Scaling)
- [Wiki: Custom Render Interfaces](https://github.com/FontStashSharp/FontStashSharp/wiki/Custom-Render-Interfaces)
- [src/SpriteFontBase.IFontStashRenderer.cs](https://github.com/FontStashSharp/FontStashSharp/blob/main/src/FontStashSharp/SpriteFontBase.IFontStashRenderer.cs), [FontSystem.cs](https://github.com/FontStashSharp/FontStashSharp/blob/main/src/FontStashSharp/FontSystem.cs), [FontSystemSettings.cs](https://github.com/FontStashSharp/FontStashSharp/blob/main/src/FontStashSharp/FontSystemSettings.cs), [SpriteFontBase.cs](https://github.com/FontStashSharp/FontStashSharp/blob/main/src/FontStashSharp/SpriteFontBase.cs)
- [NuGet: FontStashSharp.MonoGame 1.5.4](https://www.nuget.org/packages/FontStashSharp.MonoGame/)


# ===== monogame-linux =====

I have everything confirmed. The native libs are bundled in the dependency packages under `runtimes/<rid>/native/` and copied to output automatically via standard NuGet RID handling. I now have all five answers verified against primary sources.

---

# MonoGame .NET 8 DesktopGL on Ubuntu - Build-Ready Reference (no MGCB)

## 1. Version & framework compatibility

- **Latest stable: `MonoGame.Framework.DesktopGL` 3.8.4.1** (released 2025-10-20). Latest prerelease is `3.8.5-preview.6`.
- **Targets `net8.0`.** The package's only managed TFM is `net8.0`.
- **net9.0 consuming projects work.** A `net8.0` library is fully consumable from a `net9.0` app. The official template even ships `<TargetFramework>net9.0</TargetFramework>` + `<RollForward>Major</RollForward>` while the package itself is `net8.0`. Use `net8.0` for your app TFM if you want to stay on .NET 8; both run identically.
- Transitive dependencies (auto-pulled, this is how natives arrive): `MonoGame.Library.SDL` (>= 2.32.2.1), `MonoGame.Library.OpenAL` (>= 1.24.3.2), `NVorbis` (>= 0.10.4).

## 2. Minimal .csproj (no content pipeline)

The official template's `OutputType` is `WinExe` (suppresses a console window on Windows; harmless on Linux). Use `Exe` if you want a console attached. Drop `MonoGame.Content.Builder.Task` entirely since you load assets at runtime - nothing else needs to change.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <RollForward>Major</RollForward>
    <PublishReadyToRun>false</PublishReadyToRun>
    <TieredCompilation>false</TieredCompilation>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="MonoGame.Framework.DesktopGL" Version="3.8.4.1" />
    <!-- NO MonoGame.Content.Builder.Task: we load fonts/textures at runtime -->
  </ItemGroup>
</Project>
```

`RollForward=Major` lets the net8.0-targeted framework run on a net9.0+ runtime if that's all that's installed. `TieredCompilation=false` matches the template (smoother frame timing). Omit `app.manifest`/`ApplicationIcon` - they're Windows-only and not needed on Linux.

## 3. Minimal Program.cs + Game skeleton (runtime 1x1 white texture)

`Program.cs`:
```csharp
using var game = new MyApp.MainGame();
game.Run();
```

`MainGame.cs`:
```csharp
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace MyApp;

public class MainGame : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private SpriteBatch _spriteBatch = null!;
    private Texture2D _pixel = null!;   // 1x1 white, our solid-rect/line brush

    public MainGame()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 720,
            // For Mesa/software rendering or VMs, dropping HiDef avoids unsupported caps:
            GraphicsProfile = GraphicsProfile.Reach,
            SynchronizeWithVerticalRetrace = true,
        };
        Content.RootDirectory = "Content"; // unused if you never call Content.Load
        IsMouseVisible = true;

        Window.Title = "Node Graph Editor";
        Window.AllowUserResizing = true;             // resizable window
        Window.ClientSizeChanged += OnClientSizeChanged;
    }

    private void OnClientSizeChanged(object? sender, System.EventArgs e)
    {
        // Keep backbuffer in sync with the resized window.
        if (Window.ClientBounds.Width == 0 || Window.ClientBounds.Height == 0) return;
        _graphics.PreferredBackBufferWidth = Window.ClientBounds.Width;
        _graphics.PreferredBackBufferHeight = Window.ClientBounds.Height;
        _graphics.ApplyChanges();
    }

    protected override void Initialize()
    {
        // Alternative way to set backbuffer size before first frame:
        // _graphics.PreferredBackBufferWidth = 1600;
        // _graphics.PreferredBackBufferHeight = 900;
        // _graphics.ApplyChanges();
        base.Initialize();
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);

        // 1x1 white texture created entirely at runtime (no content pipeline).
        _pixel = new Texture2D(GraphicsDevice, 1, 1, mipmap: false, SurfaceFormat.Color);
        _pixel.SetData(new[] { Color.White });
    }

    protected override void Update(GameTime gameTime)
    {
        if (Keyboard.GetState().IsKeyDown(Keys.Escape))
            Exit();
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(24, 24, 28));

        // BlendState.AlphaBlend = standard premultiplied-style alpha for HUD panels.
        // Tint colors should be premultiplied (e.g. Color.White * 0.5f does this).
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend,
                           SamplerState.PointClamp);

        // Solid filled rect (HUD panel) - stretch the 1px texture:
        _spriteBatch.Draw(_pixel, new Rectangle(20, 20, 240, 120),
                          Color.White * 0.85f);

        _spriteBatch.End();
        base.Draw(gameTime);
    }
}
```

Notes: `_pixel.SetData(new[] { Color.White })` is the canonical runtime-texture idiom. `Color.White * 0.5f` produces premultiplied alpha (`(128,128,128,128)`), which is what `BlendState.AlphaBlend` expects - multiplying a tint by a float scales all four channels, so it stays premultiplied. Use `BlendState.NonPremultiplied` if you load PNGs whose alpha is straight.

## 4. Drawing rotated/scaled quads and lines with a 1px texture

All of these use the `SpriteBatch.Draw` overload with origin/rotation/scale. For a 1px source texture, `scale` directly equals pixel dimensions.

```csharp
// Axis-aligned filled rect (panel/fill):
spriteBatch.Draw(pixel, new Rectangle(x, y, w, h), color);

// 1px-thick outline of a rectangle:
static void DrawRectOutline(SpriteBatch sb, Texture2D px, Rectangle r, Color c, int t = 1)
{
    sb.Draw(px, new Rectangle(r.Left,           r.Top,            r.Width, t),        c); // top
    sb.Draw(px, new Rectangle(r.Left,           r.Bottom - t,     r.Width, t),        c); // bottom
    sb.Draw(px, new Rectangle(r.Left,           r.Top,            t,       r.Height), c); // left
    sb.Draw(px, new Rectangle(r.Right - t,      r.Top,            t,       r.Height), c); // right
}

// THE line helper - draw a line of given thickness between two points.
// Works for node-graph wires. Rotation handles any angle; scale.X = length,
// scale.Y = thickness; origin (0, 0.5) centers the 1px texture vertically on the line.
static void DrawLine(SpriteBatch sb, Texture2D px, Vector2 a, Vector2 b,
                     Color color, float thickness = 2f)
{
    Vector2 delta = b - a;
    float length = delta.Length();
    if (length < 0.0001f) return;
    float rotation = MathF.Atan2(delta.Y, delta.X);

    sb.Draw(
        px,
        position: a,
        sourceRectangle: null,
        color: color,
        rotation: rotation,
        origin: new Vector2(0f, 0.5f),                 // pivot at left edge, vertically centered
        scale: new Vector2(length, thickness),         // X = pixel length, Y = pixel thickness
        effects: SpriteEffects.None,
        layerDepth: 0f);
}

// Arbitrary rotated/scaled quad (e.g. a tilted HUD chip):
static void DrawQuad(SpriteBatch sb, Texture2D px, Vector2 center, Vector2 size,
                     float rotationRadians, Color color)
{
    sb.Draw(
        px,
        position: center,
        sourceRectangle: null,
        color: color,
        rotation: rotationRadians,
        origin: new Vector2(0.5f, 0.5f),               // rotate about the quad's center
        scale: size,                                   // (width, height) in pixels
        effects: SpriteEffects.None,
        layerDepth: 0f);
}
```

For curved bezier wires, sample the curve into N points and chain `DrawLine` between consecutive points. Use `SamplerState.PointClamp` in `Begin` so the stretched 1px texture stays crisp.

## 5. Native dependencies on Linux

**The NuGet packages bundle the natives - you do not ship or apt-install SDL/OpenAL yourself for a self-contained-style layout.** Verified by unpacking the current packages:

- `MonoGame.Library.SDL` 2.32.10.2 contains `runtimes/linux-x64/native/libSDL2-2.0.so.0` (and `linux-arm64`, `win-x64`, `win-arm64`, `osx`).
- `MonoGame.Library.OpenAL` 1.24.3.4 contains `runtimes/linux-x64/native/libopenal.so` (plus arm64/win/osx/android variants).

These are standard `runtimes/<RID>/native/` assets, so on a normal `dotnet build`/`dotnet run` they are copied next to your executable automatically for the active RID. **OpenGL itself is NOT bundled** - it's provided by the system (Mesa/`libGL`).

System packages you still want present on a clean Ubuntu (these provide the GL/loader side; SDL+OpenAL come from NuGet but pulling the apt SDL2 doesn't hurt):
```bash
sudo apt-get install -y libgl1 libglu1-mesa libsdl2-2.0-0 libopenal1
```
DesktopGL is **64-bit only** (`linux-x64` / `linux-arm64`). There is no 32-bit native.

Gotchas:
- **Mesa / software (llvmpipe) rendering:** set `GraphicsProfile.Reach` (shown above) to avoid requesting HiDef-only caps. If GL context creation still fails, force software GL with `LIBGL_ALWAYS_SOFTWARE=1`, or pin a version with `MESA_GL_VERSION_OVERRIDE=3.0` / `MESA_GLSL_VERSION_OVERRIDE=130` (MonoGame needs GL 2.0 + `ARB_framebuffer_object`, or 3.0+).
- **Headless / no DISPLAY:** SDL needs a display. Over SSH or CI, export `DISPLAY=:0` against a real X server, or run under `xvfb-run -a dotnet run`. Wayland-only sessions are fine - SDL2 falls back to its X11/Wayland backend; force a backend with `SDL_VIDEODRIVER=x11` or `wayland` if window creation misbehaves.
- **`DllNotFoundException: soft_oal.dll` / OpenAL not found at runtime** is the classic symptom of the `runtimes/` natives not being copied - happens if you flatten/`--self-contained` incorrectly or copy only the managed DLL. Publish with the RID so natives land beside the binary: `dotnet publish -c Release -r linux-x64 --self-contained`. After publish, `libSDL2-2.0.so.0` and `libopenal.so` must sit next to your app DLL.

Sources:
- [NuGet: MonoGame.Framework.DesktopGL](https://www.nuget.org/packages/MonoGame.Framework.DesktopGL/) (3.8.4.1, net8.0, deps on Library.SDL/OpenAL/NVorbis)
- [MonoGame 3.8.4.1 release notes](https://monogame.net/blog/2025-10-20-MonoGame3841-release/)
- [MonoGame docs: Supported Platforms](https://docs.monogame.net/articles/getting_started/platforms.html) (DesktopGL = SDL + OpenGL + OpenAL-Soft, Linux 64-bit only)
- [MonoGame docs: Upgrading 3.8.x to latest](https://docs.monogame.net/articles/migration/migrate_38.html) (net9 + RollForward, remove RestoreDotNetTools)
- Official DesktopGL app template csproj/Program.cs/Game1.cs: `MonoGame/MonoGame.Templates` repo, `CSharp/content/MonoGame.Application.DesktopGL.CSharp/`
- Native bundling verified by unpacking `MonoGame.Library.SDL` 2.32.10.2 and `MonoGame.Library.OpenAL` 1.24.3.4 from nuget.org (contain `runtimes/linux-x64/native/libSDL2-2.0.so.0` and `libopenal.so`).


# ===== ircv3 =====

I now have complete, authoritative wire-format details. Here are the build-ready reference notes.

---

# IRCv3 Client Implementation Reference (wire-format-first)

## 1. Connection Registration Sequence

Immediately after TCP/TLS connect, send (CRLF-terminated, `\r\n`):

```
CAP LS 302
NICK <nick>
USER <user> 0 * :<realname>
```
- `CAP LS 302` MUST be the first line you send - `302` signals you support CAP v3.2 (multiline LS, `=value` cap values). Sending it suspends registration: the server will NOT emit `001` until you send `CAP END`.
- `PASS <password>` (optional server password) must come before NICK/USER if used.
- `USER <user> 0 * :<realname>` - the two middle params are historically `<mode> <unused>`; modern servers ignore them, conventionally `0 *`.

Server replies with capability list (possibly multiline; non-final lines have a `*`):
```
CAP * LS * :multi-prefix sasl=PLAIN,EXTERNAL server-time ...
CAP * LS :batch labeled-response ...
```
Parse the space-separated names; a `=` (e.g. `sasl=PLAIN,EXTERNAL`) gives comma-separated cap values.

Request the caps you want, then end:
```
CAP REQ :multi-prefix server-time message-tags sasl
```
Server answers atomically per REQ line:
```
CAP * ACK :multi-prefix server-time message-tags sasl
CAP * NAK :<caps>      (ALL requested caps refused as a unit; none enabled)
```
After ACK (and after SASL completes, if doing it):
```
CAP END
```
→ server proceeds to `001`.

### SASL PLAIN interleave
`sasl` MUST be ACKed before `AUTHENTICATE`, and you MUST hold `CAP END` until SASL finishes. Sequence:
```
C: CAP REQ :sasl
S: CAP * ACK :sasl
C: AUTHENTICATE PLAIN
S: AUTHENTICATE +                         ← server ready for payload
C: AUTHENTICATE <base64( authzid \0 authcid \0 passwd )>
S: 900 <nick> <nick>!user@host <account> :You are now logged in as <account>
S: 903 <nick> :SASL authentication successful
C: CAP END
```
- Payload = `\0username\0password` (empty authzid, so a leading NUL), UTF-8, then base64. Example: `jilles`/`sesame` → `amlsbGVzAGppbGxlcwBzZXNhbWU=`.
- If base64 payload > 400 bytes, split into ≤400-byte `AUTHENTICATE` chunks; if the data length is an exact multiple of 400, send a trailing `AUTHENTICATE +` to signal end.
- Abort with `AUTHENTICATE *`.
- SASL numerics: **900** RPL_LOGGEDIN, **901** RPL_LOGGEDOUT, **902** ERR_NICKLOCKED, **903** RPL_SASLSUCCESS (proceed to CAP END), **904** ERR_SASLFAIL (bad creds), **905** ERR_SASLTOOLONG, **906** ERR_SASLABORTED, **907** ERR_SASLALREADY, **908** RPL_SASLMECHS (`908 <nick> <mech,mech> :...` - server lists supported mechs; PLAIN was unavailable). On 904/905/906/908: either retry/abort, then still send `CAP END` to finish registration unauthenticated (or disconnect, per policy).

## 2. Message Format & Message-Tags ABNF

```
message     ::= ['@' tags SPACE] [':' source SPACE] command [params] crlf
SPACE       ::= %x20 *(%x20)                 ; one or more spaces
crlf        ::= %x0D %x0A
params      ::= *( SPACE middle ) [ SPACE ':' trailing ]
middle      ::= nospcrlfcl *( ':' / nospcrlfcl )   ; no leading ':', no spaces
trailing    ::= *( ':' / ' ' / nospcrlfcl )        ; may contain ':' and ' '
nospcrlfcl  ::= any octet except NUL, CR, LF, ' ', ':'

tags        ::= tag *( ';' tag )
tag         ::= key [ '=' escaped_value ]
key         ::= [ '+' ] [ vendor '/' ] key_name      ; '+' = client-only tag
key_name    ::= 1*( ALPHA / DIGIT / '-' )
vendor      ::= host (e.g. draft, example.com)
escaped_value ::= zero+ UTF-8 octets except NUL CR LF ';' SPACE (use escapes for those)
```

Tag-value escape table (escape on send, unescape on receive):

| Raw char | Escaped |
|----------|---------|
| `;`      | `\:`    |
| SPACE    | `\s`    |
| `\`      | `\\`    |
| CR       | `\r`    |
| LF       | `\n`    |
| (other)  | itself  |

Unescape note: a lone trailing `\` and any `\x` not in the table → drop the backslash, keep `x` (or drop trailing `\`). A key with no `=` is a value-less tag (treat as empty/true). Example: `@time=2011-10-19T16:40:51.620Z;+example/foo=bar\swith\sspaces :nick!u@h PRIVMSG #c :hi`.

### Robust parse algorithm
1. If line starts with `@`: split off tag-segment up to first space; drop the `@`; split on `;`; each part split on first `=` → key, escaped value; unescape value. Strip leading `+` to flag client-only.
2. Advance past spaces. If next char is `:`: read `source` (prefix) up to next space, drop the `:`. Prefix form is `nick!user@host` (or just `servername`); split on `!` then `@`.
3. Read `command` (a word, or 3-digit numeric) up to next space.
4. Loop params: skip spaces; if current token starts with `:`, the rest of the line (after the `:`) is the single `trailing` param (may contain spaces/colons) - stop. Otherwise read one `middle` token and continue.
5. Treat trailing identically to other params at the API level; the `:` is purely a wire marker. Don't assume a trailing exists. Total line ≤ 512 bytes without tags; tags add up to 8191 bytes for the client-visible portion.

## 3. Useful Caps to Request

| Cap | What it does | Support |
|-----|--------------|---------|
| `message-tags` | Enables generic tag transport (incl. client-only `+` tags) on all messages; prerequisite for several tag features | Wide |
| `server-time` | Adds `@time=YYYY-MM-DDThh:mm:ss.sssZ` (UTC, ms precision) tag to messages - real timestamps esp. from bouncers/history | Very wide |
| `account-tag` | Adds `@account=<accountname>` tag to messages from logged-in users | Wide |
| `account-notify` | Sends `ACCOUNT <accountname>` (or `ACCOUNT *` on logout) when a visible user logs in/out | Wide |
| `away-notify` | Sends `AWAY :<msg>` / bare `AWAY` when a visible user's away state changes (no manual WHO polling) | Wide |
| `extended-join` | JOIN gains account + realname params (see §5) | Wide |
| `multi-prefix` | NAMES/WHO show ALL status prefixes (`@+nick`), not just the highest | Very wide |
| `sasl` | Enables `AUTHENTICATE` for SASL auth during registration (value lists mechs, e.g. `sasl=PLAIN,EXTERNAL`) | Very wide |
| `chghost` | Server sends `CHGHOST <newuser> <newhost>` instead of fake QUIT/JOIN when a user's ident/host changes | Wide |
| `echo-message` | Server echoes your own PRIVMSG/NOTICE/TAGMSG back to you (delivery ack, lag measure, bouncer sync) | Moderate-wide |
| `batch` | Groups related messages between `BATCH +<ref> <type>` … `BATCH -<ref>`; messages carry `@batch=<ref>` | Wide |
| `labeled-response` | You add `@label=<id>` to a command; server tags its response(s) with the same `@label` so you can correlate request↔reply | Moderate (Ergo, modern servers) |
| `standard-replies` | Enables structured `FAIL`/`WARN`/`NOTE` messages (machine-readable codes) - see §5 | Growing |

Most universally available: `multi-prefix`, `sasl`, `server-time`, `message-tags`, `account-notify`, `away-notify`, `extended-join`, `account-tag`, `chghost`. `labeled-response` and `standard-replies` are newer (well supported on Ergo/Solanum-class servers).

## 4. Registration Numerics & Keepalive

- `001` RPL_WELCOME - registration complete; **param 1 is your actual assigned nick** (adopt it, may differ from requested). First line you can rely on for "connected".
- `005` RPL_ISUPPORT - `005 <nick> KEY=val KEY ... :are supported by this server`; one or more lines. Parse for `PREFIX`, `CHANTYPES`, `CHANMODES`, `NICKLEN`, `CASEMAPPING`, `TARGMAX`, etc. Never assume a feature unless advertised here.
- `375`/`372`/`376` - MOTD start / line / `376` RPL_ENDOFMOTD (end). `422` ERR_NOMOTD when no MOTD. Either `376` or `422` marks the end of the initial burst.
- `433` ERR_NICKNAMEINUSE - `433 <client> <badnick> :Nickname is in use`. During registration (`<client>` is often `*`), pick an alternate and resend `NICK`. After registration it just rejects the change.
- `432` ERR_ERRONEUSNICKNAME (invalid nick), `451` ERR_NOTREGISTERED, `462` ERR_ALREADYREGISTERED - handle defensively.

Keepalive:
```
S: PING <token>
C: PONG <token>
```
Reply with the **same token**, immediately, on any PING. Tokens are arbitrary non-empty strings - echo verbatim, don't parse. You may also send your own `PING <token>` and expect `PONG [<server>] <token>` (servers include a server param; ignore it). If no traffic for an idle interval, send a PING and disconnect if no PONG within your timeout. `ERROR :<reason>` from the server means it's closing the link.

## 5. Outgoing Command Formats

Never send a source prefix from a client. CRLF-terminate every line.

```
PRIVMSG <target> :<text>            target = #channel or nick; ':' lets text contain spaces
NOTICE  <target> :<text>            like PRIVMSG; bots/auto-responders SHOULD NOT auto-reply to NOTICE
JOIN    <chan>{,<chan>} [<key>{,<key>}]    e.g. JOIN #a,#b key1,   ;  JOIN 0 parts all
PART    <chan>{,<chan>} [:<reason>]
NICK    <newnick>
MODE    <chan> <modestring> [<args>]       e.g. MODE #ch +o nick ; MODE #ch +b *!*@host
MODE    <nick> <modestring>                user modes, e.g. MODE mynick +i
TOPIC   <chan>                             query current topic
TOPIC   <chan> :<new topic>                set; TOPIC <chan> :   clears it
KICK    <chan> <user>{,<user>} [:<reason>]
```
- Targets/multi-target limits come from `005` `TARGMAX`. Use trailing `:` on any param that may contain spaces (text/reason/topic) and on the last param to be safe.

### Sending tagged messages (client-only `+` tags)
Requires `message-tags` ACKed. Prepend `@` + `;`-joined tags + one space. Client-only tags carry a `+` prefix and are relayed to other clients (servers ignore unknown ones). Escape values per §2 table.

```
@+draft/reply=<msgid> PRIVMSG #chan :see above
@+typing=active TAGMSG #chan
@+typing=done   TAGMSG #chan
@label=42 @+draft/react=👍 TAGMSG #chan       (label for labeled-response correlation)
```
- `TAGMSG <target>` is a message that carries only tags, no body - use it for `+typing`, reactions, etc.
- `+typing` values: `active`, `paused`, `done` (draft spec). `+draft/reply=<message-id>` references the parent message's `msgid` tag.
- To correlate replies, add `@label=<id>` (server echoes it back under `labeled-response`); combine with `echo-message` for self-echo confirmation.

### Inbound structured replies (standard-replies)
```
<FAIL|WARN|NOTE> <command|*> <CODE> [<context>...] :<human description>
```
e.g. `FAIL * NEED_REGISTRATION :You need to be registered`, `FAIL ACC REG_INVALID_CALLBACK REGISTER :Email address is not valid`. `<command>` is `*` if not tied to a command; `<CODE>` is the machine-readable key to branch on; everything after the final `:` is display text.

---

Sources:
- [modern.ircdocs.horse - Modern IRC Client Protocol](https://modern.ircdocs.horse/)
- [IRCv3 - Capability Negotiation](https://ircv3.net/specs/extensions/capability-negotiation.html)
- [IRCv3 - SASL 3.1](https://ircv3.net/specs/extensions/sasl-3.1.html)
- [IRCv3 - Message Tags](https://ircv3.net/specs/extensions/message-tags.html)
- [IRCv3 - server-time](https://ircv3.net/specs/extensions/server-time.html)
- [IRCv3 - extended-join](https://ircv3.net/specs/extensions/extended-join.html)
- [IRCv3 - Standard Replies](https://ircv3.net/specs/extensions/standard-replies.html)
- [IRCv3 - echo-message](https://ircv3.net/specs/extensions/echo-message.html)
- [IRCv3 - Labeled Responses](https://ircv3.net/specs/extensions/labeled-response.html)
- [IRCv3 Specifications index](https://ircv3.net/irc/)


# ===== nodegraph-ux =====

This is a research/knowledge task - the user wants build-ready implementation notes with concrete math and code patterns. No codebase exploration is needed since this is about general node-graph editor implementation. Let me write up tight, code-first notes.

# Node-Graph Editor - Build-Ready Implementation Notes (MonoGame SpriteBatch, immediate mode)

## 1. Camera: pan/zoom world↔screen transform

State: `Vector2 Pan` (screen-space offset, in pixels), `float Zoom` (scale). Convention: **world → screen = `world * Zoom + Pan`**.

```csharp
struct Camera {
    public Vector2 Pan;   // screen px
    public float   Zoom = 1f;

    public Vector2 WorldToScreen(Vector2 w) => w * Zoom + Pan;
    public Vector2 ScreenToWorld(Vector2 s) => (s - Pan) / Zoom;

    // SpriteBatch matrix (pass to SpriteBatch.Begin(transformMatrix: M)).
    // Then you draw nodes in WORLD coords and the GPU applies the transform.
    public Matrix Matrix =>
        Matrix.CreateScale(Zoom, Zoom, 1f) * Matrix.CreateTranslation(Pan.X, Pan.Y, 0f);
}
```

Two valid approaches - pick one and be consistent:
- **A. Matrix mode (recommended):** `SpriteBatch.Begin(transformMatrix: cam.Matrix)`. Draw everything in world coords; GPU scales/translates. Font/line thickness scale automatically (can look soft when zoomed - see grid note).
- **B. Manual mode:** keep `SpriteBatch.Begin()` at identity, and call `WorldToScreen` yourself per primitive. More control over text crispness/line width, more code. Hit-testing math is identical either way (do it in world space).

**Pan (drag):** on mouse move while panning, `Pan += mouseNow - mousePrev;` (raw screen delta - no zoom factor, because Pan is in screen px).

**Zoom-to-cursor** (the key formula - keep the world point under the cursor fixed across the zoom):

```csharp
void ZoomAt(Vector2 cursorScreen, float wheelDelta) {
    float oldZoom = Zoom;
    float factor  = MathF.Pow(1.1f, wheelDelta); // wheelDelta = +1/-1 per notch
    Zoom = Math.Clamp(oldZoom * factor, MinZoom, MaxZoom);

    // World point under cursor must stay invariant:
    //   cursor = wWorld*oldZoom + oldPan  AND  cursor = wWorld*newZoom + newPan
    // Solve for newPan:
    Vector2 wWorld = (cursorScreen - Pan) / oldZoom;
    Pan = cursorScreen - wWorld * Zoom;
}
```

Use `MinZoom ≈ 0.1`, `MaxZoom ≈ 4`. Note MonoGame `MouseState.ScrollWheelValue` is cumulative; track `delta = (cur - prev) / 120f`.

---

## 2. Infinite grid (major/minor, pans + zooms, only visible lines)

Compute the visible world rect from screen corners, then step by grid spacing. Draw lines as 1px-thick stretched textures (a 1×1 white pixel) - never one draw per pixel.

```csharp
// 1x1 white texture created once: pixel.SetData(new[]{Color.White});

void DrawGrid(SpriteBatch sb, Camera cam, Rectangle viewport) {
    const float baseStep = 32f;     // world units between minor lines
    const int   majorEvery = 5;     // every 5th line is "major"

    // Adapt spacing so lines never get too dense/sparse: pick a power-of-2/5
    // multiplier so on-screen spacing stays in a comfortable px range.
    float step = baseStep;
    while (step * cam.Zoom < 8f)  step *= majorEvery;   // too dense -> coarsen
    while (step * cam.Zoom > 80f) step /= majorEvery;   // too sparse -> refine

    Vector2 topLeft = cam.ScreenToWorld(new Vector2(viewport.Left,  viewport.Top));
    Vector2 botRight= cam.ScreenToWorld(new Vector2(viewport.Right, viewport.Bottom));

    float x0 = MathF.Floor(topLeft.X / step) * step;
    float y0 = MathF.Floor(topLeft.Y / step) * step;

    Color minor = new Color(40,40,45), major = new Color(70,70,80);

    for (float x = x0; x <= botRight.X; x += step) {
        bool isMajor = Mod(x, step*majorEvery) == 0;       // see Mod note below
        float sx = cam.WorldToScreen(new Vector2(x,0)).X;  // vertical line
        DrawVLine(sb, sx, viewport.Top, viewport.Bottom, isMajor ? major : minor);
    }
    for (float y = y0; y <= botRight.Y; y += step) {
        bool isMajor = Mod(y, step*majorEvery) == 0;
        float sy = cam.WorldToScreen(new Vector2(0,y)).Y;  // horizontal line
        DrawHLine(sb, sy, viewport.Left, viewport.Right, isMajor ? major : minor);
    }
}

// Draw thin lines with the 1px texture (identity-space SpriteBatch).
void DrawVLine(SpriteBatch sb, float x, float y0, float y1, Color c) =>
    sb.Draw(pixel, new Rectangle((int)x, (int)y0, 1, (int)(y1-y0)), c);
void DrawHLine(SpriteBatch sb, float y, float x0, float x1, Color c) =>
    sb.Draw(pixel, new Rectangle((int)x0, (int)y, (int)(x1-x0), 1), c);
```

Notes:
- Draw the grid in **screen space at identity** (compute screen X/Y from world via `WorldToScreen`) so lines stay crisp 1px regardless of zoom. Don’t draw it inside the world-matrix batch or 1px lines blur.
- `Mod` for "is this a major line" must handle floats robustly: `bool IsMajor(float v,float major)=> MathF.Abs(MathF.Round(v/major)*major - v) < 1e-3f;`. Counting by integer index from `x0` is cleaner: `int idx = (int)MathF.Round(x/step); isMajor = idx % majorEvery == 0;`.
- Cost is O(visible lines), independent of world size - the floor/ceil clamp to the viewport is what makes it "infinite."
- Optional 3rd tier: draw axis lines (world x=0, y=0) in a brighter color.

---

## 3. Hit-testing & dragging (nodes, ports, wires)

Do **all** hit-testing in **world space** - convert the mouse once: `Vector2 mw = cam.ScreenToWorld(mouseScreen);`. Then your node rects/port circles are zoom-independent.

```csharp
class Port { public int NodeId; public bool IsInput; public Vector2 LocalOffset; }
class Node {
    public int Id; public Vector2 Pos; public Vector2 Size; // world
    public List<Port> Inputs, Outputs;
    public Rectangle Rect => new((int)Pos.X,(int)Pos.Y,(int)Size.X,(int)Size.Y);

    // Ports laid out down the left (inputs) / right (outputs) edges.
    public Vector2 PortWorld(Port p) => Pos + p.LocalOffset;
}

const float PortRadius = 6f;          // world units
const float PortHitR   = 10f;         // a bit larger than visual for forgiving hits

bool PointInNode(Vector2 mw, Node n) => n.Rect.Contains((int)mw.X,(int)mw.Y);
bool PointOnPort(Vector2 mw, Vector2 portWorld) =>
    Vector2.DistanceSquared(mw, portWorld) <= PortHitR*PortHitR;
```

**Pick order matters** (topmost / smallest target first): test **ports before node body**, and iterate nodes **front-to-back** (reverse z-order) so the top node wins.

```csharp
HitResult Pick(Vector2 mw) {
    for (int i = nodes.Count-1; i >= 0; i--) {   // front-to-back
        var n = nodes[i];
        foreach (var p in n.Outputs) if (PointOnPort(mw, n.PortWorld(p))) return Hit.Port(n,p);
        foreach (var p in n.Inputs)  if (PointOnPort(mw, n.PortWorld(p))) return Hit.Port(n,p);
        if (PointInNode(mw, n)) return Hit.Node(n);
    }
    return Hit.Empty();
}
```

**Dragging a node:** on mousedown over node body, record `dragOffset = n.Pos - mw` (world). On move: `n.Pos = mw + dragOffset`. Using the offset (not delta) avoids drift. For multi-drag, store per-selected `grabPos[id] = n.Pos - mw` and apply the same.

**Starting / dropping a wire:**
- Mousedown on an **output** port → enter `DraggingWire`, remember `(srcNode, srcPort)`. The wire’s free end follows `mw`.
- On move, render a bezier from `srcPort` to `mw`. Optionally highlight the nearest **compatible input** port within hit radius (snap target).
- Mouseup: `Pick(mw)`; if it’s an **input** port and types are compatible and not the same node → create connection. If an input already has a wire, replace it (inputs usually accept one; outputs fan out to many).
- Also support the reverse (drag from an input that already has a wire to "pull it off").

```csharp
class Connection { public Port From; /*output*/ public Port To; /*input*/ }

void OnWireDrop(Vector2 mw) {
    var hit = Pick(mw);
    if (hit.IsPort && hit.Port.IsInput && hit.Node.Id != pendingSrc.NodeId
        && TypesCompatible(pendingSrc, hit.Port)) {
        connections.RemoveAll(c => c.To == hit.Port);    // input = single
        connections.Add(new Connection { From = pendingSrc, To = hit.Port });
    }
    state = State.Idle;
}
```

---

## 4. Bezier wire rendering

Place control points with **horizontal tangents** so wires leave outputs going right and enter inputs going left - the flow-editor look.

```csharp
// p0 = output port world pos, p3 = input port world pos
void WireControlPoints(Vector2 p0, Vector2 p3, out Vector2 c1, out Vector2 c2) {
    float dx = MathF.Abs(p3.X - p0.X);
    // Tangent length: proportional to horizontal gap, clamped so short/backward
    // wires still bow out nicely. Tune 0.5 and the [40,200] clamp to taste.
    float t = Math.Clamp(dx * 0.5f, 40f, 200f);
    c1 = p0 + new Vector2( t, 0);   // leave output rightward
    c2 = p3 + new Vector2(-t, 0);   // enter input from the left
}

static Vector2 Cubic(Vector2 p0,Vector2 c1,Vector2 c2,Vector2 p3,float u){
    float v=1-u;
    return v*v*v*p0 + 3*v*v*u*c1 + 3*v*u*u*c2 + u*u*u*p3;
}
```

**Tessellate → line segments** (SpriteBatch has no native curve; draw N stretched-pixel segments). Scale segment count with on-screen length so far-away wires are cheap:

```csharp
void DrawWire(SpriteBatch sb, Camera cam, Vector2 p0, Vector2 p3, Color col, float widthPx=2f){
    WireControlPoints(p0,p3, out var c1, out var c2);
    // estimate screen length to choose segment count
    float approxLen = (cam.WorldToScreen(p3) - cam.WorldToScreen(p0)).Length();
    int N = Math.Clamp((int)(approxLen / 12f), 12, 64);

    Vector2 prev = cam.WorldToScreen(p0);
    for (int i = 1; i <= N; i++) {
        float u = i / (float)N;
        Vector2 cur = cam.WorldToScreen(Cubic(p0,c1,c2,p3,u));
        DrawLineSeg(sb, prev, cur, col, widthPx);
        prev = cur;
    }
}

// Thick line between two screen points via rotated 1px texture.
void DrawLineSeg(SpriteBatch sb, Vector2 a, Vector2 b, Color c, float thick){
    Vector2 d = b - a; float len = d.Length(); if (len < 1e-4f) return;
    float ang = MathF.Atan2(d.Y, d.X);
    sb.Draw(pixel, a, null, c, ang, new Vector2(0, 0.5f),
            new Vector2(len, thick), SpriteEffects.None, 0f);
}
```

Tips: round joints look fine at N≥16; for extra-smooth wires draw with a soft/AA 1px texture or a small circle at each vertex. Use **adaptive flatness** instead of fixed N if you want pixel-perfect: subdivide while the midpoint deviates from the chord by > 0.3px.

---

## 5. Selection, box-select, z-order, interaction state machine

**Z-order:** keep `nodes` as a list ordered back→front. Draw in order (front last). On click/drag-start of a node, move it to the end (`nodes.Remove(n); nodes.Add(n);`) so it renders and hit-tests on top. Hit-test iterates **reverse**.

**Selection set:** `HashSet<int> selected`. Click empty = clear (unless shift). Click node = select it (shift toggles). Dragging a selected node drags the whole set.

**Box / marquee select:** on mousedown over empty space → `BoxSelect`. Track `boxStartWorld`. Each frame the box = `RectFromCorners(boxStartWorld, mw)`. On release, select all nodes whose rect intersects the box (or is fully contained - pick a rule; some editors use intersect for drag-left, contain for drag-right). Shift adds to existing selection.

```csharp
Rectangle RectFromCorners(Vector2 a, Vector2 b){
    int x=(int)MathF.Min(a.X,b.X), y=(int)MathF.Min(a.Y,b.Y);
    return new Rectangle(x,y,(int)MathF.Abs(a.X-b.X),(int)MathF.Abs(a.Y-b.Y));
}
```

**State machine** - one enum drives all mouse handling; transitions happen on mousedown given the pick result:

```csharp
enum State { Idle, DraggingNode, DraggingWire, Panning, BoxSelect }

void OnMouseDown(MouseState m, KeyboardState kb) {
    Vector2 ms = new(m.X, m.Y);
    Vector2 mw = cam.ScreenToWorld(ms);

    // Middle mouse (or space+left) always pans, regardless of what's under cursor.
    if (m.MiddleButton == Pressed || (kb.IsKeyDown(Space) && m.LeftButton==Pressed)) {
        state = State.Panning; panAnchor = ms; return;
    }
    if (m.LeftButton != Pressed) return;

    var hit = Pick(mw);
    switch (hit.Kind) {
        case Hit.PortKind:
            pendingSrc = hit.Port; state = State.DraggingWire; break;
        case Hit.NodeKind:
            BringToFront(hit.Node);
            if (!selected.Contains(hit.Node.Id)) {
                if (!kb.IsKeyDown(Shift)) selected.Clear();
                selected.Add(hit.Node.Id);
            }
            // record per-selected grab offsets for multi-drag
            foreach (var id in selected) grab[id] = nodes[id].Pos - mw;
            state = State.DraggingNode; break;
        default: // empty space
            if (!kb.IsKeyDown(Shift)) selected.Clear();
            boxStart = mw; state = State.BoxSelect; break;
    }
}

void OnMouseMove(Vector2 ms) {
    Vector2 mw = cam.ScreenToWorld(ms);
    switch (state) {
        case State.Panning:      cam.Pan += ms - panAnchor; panAnchor = ms; break;
        case State.DraggingNode: foreach (var id in selected) nodes[id].Pos = mw + grab[id]; break;
        case State.DraggingWire: wireEnd = mw; /* + compute snap target for highlight */ break;
        case State.BoxSelect:    boxCur = mw; break;
    }
}

void OnMouseUp(Vector2 ms) {
    Vector2 mw = cam.ScreenToWorld(ms);
    if (state == State.DraggingWire) OnWireDrop(mw);
    if (state == State.BoxSelect) {
        var box = RectFromCorners(boxStart, mw);
        foreach (var n in nodes) if (box.Intersects(n.Rect)) selected.Add(n.Id);
    }
    state = State.Idle;
}

// Wheel handled separately, any state:  ZoomAt(new Vector2(m.X,m.Y), wheelDelta);
```

**Draw order each frame** (back to front, all in world space unless noted):
1. Grid (screen-space, identity batch - see §2).
2. Wires (behind nodes so cards overlap them cleanly).
3. The in-progress wire (`pendingSrc → wireEnd`) if `DraggingWire`.
4. Nodes in list order; draw selection outline/glow for `selected` ids.
5. Marquee rectangle (semi-transparent fill + outline) if `BoxSelect`.

**Gotchas to bake in:**
- Distinguish click vs drag with a small dead-zone (e.g. 4px) before committing to `DraggingNode`/`BoxSelect`, so a plain click just selects.
- Keep `mousePrev` for pan delta; reset it on state entry to avoid a jump on the first move frame.
- Hit radii and port/node geometry are all in **world** units, so they auto-scale with zoom and the math stays clean.
- `MouseState.ScrollWheelValue` is cumulative - diff against last frame.

These five pieces (camera, grid, hit-test/drag, bezier wires, state machine + selection) are the full backbone; everything else (node content, sockets typing, copy/paste, undo) layers on top of this without changing the core transform/interaction math.