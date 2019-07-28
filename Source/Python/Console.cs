using System;
using System.Text;
using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Generic;
using Verse;
using UnityEngine;
using Harmony;
using System.Linq;

namespace Python
{
    public class LineBuffer : IEnumerable<string>
    {
        public class LineModifiedEventArgs : System.EventArgs
        {
            public int index;
            public string updatedValue;
            // if oldValue remains the default null state, it indicates the line was newly created:
            public string oldValue = null; 
        }

        public event System.EventHandler<LineModifiedEventArgs> LineModified;

        protected virtual void OnLineModified(LineModifiedEventArgs e)
        {
            LineModified?.Invoke(this, e);
        }

        protected List<string> lines = new List<string>();

        public IEnumerator<string> GetEnumerator() => lines.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();

        protected virtual void ValidateInput(string input)
        {
            if (input == null)
                throw new System.ArgumentNullException();
            if (input.Contains('\n') || input.Contains('\0'))
                throw new System.ArgumentException("Input must not contain newline or null characters");
                //throw new System.ArgumentException("Input [ "+input+" ] must not contain newline or null characters "+
                //    input.Select(c => Char.GetNumericValue(c).ToString()).Join());
        }

        public string this[int key]
        {
            get
            {
                return lines[key];
            }
            set
            {
                ValidateInput(value);
                var e = new LineModifiedEventArgs() { index = key, oldValue = lines[key] };
                e.updatedValue = lines[key] = value;
                if (e.updatedValue != e.oldValue)
                    OnLineModified(e);
            }
        }

        public void Add(string line)
        {
            ValidateInput(line);
            int index = lines.Count;
            lines.Add(line);
            OnLineModified(new LineModifiedEventArgs() { index = index, updatedValue = line });
        }

        public void AddMultiline(string text)
        {
            var newlines = new List<string>(text.SplitLines());
            foreach (string line in newlines)
                ValidateInput(line);
            int start_index = lines.Count;
            lines.AddRange(newlines);
            for (int i = 0; i < newlines.Count; i++)
                OnLineModified(new LineModifiedEventArgs() { index = start_index+i, updatedValue = newlines[i] });
        }

        public int LineCount => lines.Count;

        public LineBuffer() { }
        public LineBuffer(IEnumerable<string> lines)
        {
            foreach (string l in lines)
                Add(l);
        }
    }

    public class Console
    {
        //data
        private Microsoft.Scripting.Hosting.ScriptScope scope;
        private IronPython.Modules.PythonIOModule.StringIO python_output_buffer;
        private IronPython.Runtime.PythonFunction python_compile_command;
        private IronPython.Runtime.PythonFunction python_console_run_code;
        public LineBuffer output;
        public LineBuffer input;
        private StringBuilder editor;
        private StringBuilder multilineEditor;
        private int _currentInputIndex;
        private int _editorCursor;

        //constructor
        public Console()
        {
            scope = Python.CreateScope();
            var ops = scope.Engine.Operations;
            Python.Engine.Execute(
                "import sys"
                +"\nimport code"
                +"\nfrom code import compile_command"
                +"\nimport io"
                +"\nfrom contextlib import contextmanager"
                +"\noutput_buffer = io.StringIO()"
                +"\nsys.stdout = sys.stderr = output_buffer"
                +"\nimport clr"
                +"\nimport Verse"
                +"\ng = dict((k,v) for k,v in globals().items() if k in {'__name__','__doc__','clr','Verse'})"
                +"\ninterpreter = code.InteractiveInterpreter(g)"
                +"\nbackup_fds = []"

                + "\n@contextmanager"
                + "\ndef redirect_output(fd):"
                + "\n\tbackup_fds.append((sys.stdout, sys.stderr))"
                + "\n\tsys.stdout, sys.stderr = fd, fd"
                + "\n\tyield"
                + "\n\tsys.stdout, sys.stderr = backup_fds.pop()"

                + "\ndef console_run_code(code):"
                + "\n\twith redirect_output(output_buffer):"
                + "\n\t\treturn interpreter.runcode(code)"
                , scope);
            python_output_buffer = scope.GetVariable<IronPython.Modules.PythonIOModule.StringIO>("output_buffer");
            python_compile_command = scope.GetVariable<IronPython.Runtime.PythonFunction>("compile_command");
            python_console_run_code = scope.GetVariable<IronPython.Runtime.PythonFunction>("console_run_code");

            output = new LineBuffer(new string[] {">>> "});
            input = new LineBuffer(new string[] {""});
            editor = new StringBuilder(500);
            multilineEditor = new StringBuilder(500);
            _currentInputIndex = 0;
            _editorCursor = 0;
        }

        //properties
        public string EditorText { get { return editor.ToString(); } }
        public int EditorLength { get { return editor.Length; } }
        public int CurrentInputIndex
        {
            get
            {
                return _currentInputIndex;
            }
            set
            {
                if (value < 0 || value >= input.LineCount)
                    return;// throw new System.ArgumentOutOfRangeException();
                if (value == _currentInputIndex)
                    return;
                input[_currentInputIndex] = EditorText;
                _currentInputIndex = value;
                editor.Length = 0;
                editor.Append(input[_currentInputIndex]);
                EditorCursor = editor.Length;
            }
        }
        public int EditorCursor
        {
            get { return _editorCursor; }
            set { _editorCursor = Mathf.Max(0, Mathf.Min(value, editor.Length)); }
        }

        //functions
        public void InputChar(char c)
        {
            editor.Insert(EditorCursor, c);
            EditorCursor++;
        }

        public void DeleteChar(bool backward)
        {
            if (backward)
            {
                if (EditorCursor == 0)
                    return;
                EditorCursor--;
            }
            try
            {
                editor.Remove(EditorCursor, 1);
            }
            catch (System.ArgumentOutOfRangeException) { }
        }

        public void ProcessInput()
        {
            var ops = scope.Engine.Operations;
            string text = EditorText;
            output[output.LineCount - 1] += text;
            if (CurrentInputIndex == input.LineCount - 1 && text.Length > 0)
                input.Add("");
            CurrentInputIndex = input.LineCount - 1;

            //send input to Python
            bool complete = true;
            {
                multilineEditor.Append(text);
                object compiled;
                try
                {
                    compiled = ops.Invoke(python_compile_command, multilineEditor.ToString());
                }
                catch (Exception e)
                {
                    output.AddMultiline(e.ToString());
                    goto DonePython;
                }

                if (compiled == null)
                {
                    complete = false;
                }
                else
                {
                    try
                    {
                        ops.Invoke(python_console_run_code, compiled);
                    }
                    catch (Exception e)
                    {
                        Log.Message(e.ToString());
                    }
                }
            }
            {
                //consume output buffer
                string new_output = python_output_buffer.getvalue(IronPython.Runtime.DefaultContext.Default);
                if (new_output.Length > 0)
                {
                    output.AddMultiline(new_output);
                    python_output_buffer.truncate(IronPython.Runtime.DefaultContext.Default, 0);
                    python_output_buffer.seek(IronPython.Runtime.DefaultContext.Default, 0, 0);
                }
            }

        DonePython:
            if (complete)
            {
                multilineEditor.Length = 0;
                output.Add(">>> ");
            }
            else
            {
                multilineEditor.AppendLine();
                output.Add("... ");
            }
        }
    }

    public class ConsoleMetrics
    {
        private readonly int listScalingBits = 3;
        public readonly Console console;

        private int _columns;
        public int Columns
        {
            get { return _columns; }
            set
            {
                if (value <= 0)
                    throw new System.ArgumentOutOfRangeException("Number of columns must be greater than zero");
                _columns = value;
            }
        }

        private class OutputMetric
        {
            public int cachedColumnDim = -1;//default -1 means dirty
            public int rowCountWhenCachedColumnDim;
        }
        private List<List<OutputMetric>> metrics;

        private int lastLineIndex = -1;

        public ConsoleMetrics(Console console, int columns=80)
        {
            this.console = console;
            Columns = columns;
            metrics = new List<List<OutputMetric>>() { new List<OutputMetric>() };
            lastLineIndex = -1;
            for (int i = 0; i < console.output.LineCount; i++)
                MarkDirty(i);
            console.output.LineModified += ((o,e) => MarkDirty(e.index));
        }

        //
        private void MarkDirty(int lineIndex)
        {
            if (lineIndex > metrics[0].Count)
                throw new System.ArgumentOutOfRangeException();
            int lineLength = console.output[lineIndex].Length;
            var indexByTier = new List<int>();
            for (var lineIndexDigest = lineIndex; lineIndexDigest != 0; lineIndexDigest >>= listScalingBits)
                indexByTier.Add(lineIndexDigest);
            indexByTier.Add(0);

            if (indexByTier.Count == metrics.Count + 1)
                metrics.Add(new List<OutputMetric>());
            for (int i = 0; i < metrics.Count; i++)
            {
                int index = indexByTier[i];
                List<OutputMetric> metricTier = metrics[i];
                OutputMetric metric;

                if (index == metricTier.Count)
                    metricTier.Add(metric = new OutputMetric());
                else if (index > metricTier.Count)
                    throw new System.InvalidOperationException("Data structure has gaps");
                else
                    metric = metricTier[index];
                metric.cachedColumnDim = -1;
            }
            lastLineIndex = metrics[0].Count - 1;
        }

        private int RowsForStrLen(int strlen)
        {
            if (strlen == 0) return 1;
            return (strlen - 1) / Columns + 1;
        }

        private int RowsInRegion(int tier, int regionIndex)
        {
            OutputMetric metric = metrics[tier][regionIndex];
            if (metric.cachedColumnDim != Columns)
            {
                metric.cachedColumnDim = Columns;
                if (tier == 0)
                {
                    int len = console.output[regionIndex].Length;
                    metric.rowCountWhenCachedColumnDim = RowsForStrLen(len);
                }
                else
                {
                    tier--;
                    regionIndex <<= listScalingBits;
                    int regionSize = Math.Min(1 << listScalingBits, metrics[tier].Count - regionIndex);
                    var regionIndices = Enumerable.Range(regionIndex, regionSize);
                    metric.rowCountWhenCachedColumnDim = regionIndices.Sum(i => RowsInRegion(tier, i));
                }
            }
            return metric.rowCountWhenCachedColumnDim;
        }

        public int ExtraEditorRows
        {
            get
            {
                int lastLineLen = (metrics[0].Count > 0) ? RowsInRegion(0, lastLineIndex) : 0;
                return RowsForStrLen(lastLineLen + console.EditorLength) - RowsForStrLen(lastLineLen);
            }
        }

        public int TotalRows()
        {
            int allOutputRows;
            if (metrics[0].Count == 0)
            {
                allOutputRows = 0;
            }
            else
            {
                int topTierIndex = metrics.Count - 1;
                if (metrics[topTierIndex].Count != 1)
                    throw new System.InvalidOperationException("Broadest metrics tier does not have exactly one element");
                allOutputRows = RowsInRegion(topTierIndex, 0);
            }
            return allOutputRows + ExtraEditorRows;
        }

        public struct RowInfo
        {
            public ConsoleMetrics ConsoleMetrics;
            public int rowIndex;
            public int rowSubindexInLine;
            public int lineIndex;
            public bool isAtLastLine;

            public int CharacterOffset {
                get { return rowSubindexInLine * this.ConsoleMetrics.Columns; }
            }
            public string Line {
                get {
                    string r = this.ConsoleMetrics.console.output[lineIndex];
                    if (isAtLastLine)
                        r += this.ConsoleMetrics.console.EditorText;
                    return r;
                }
            }
            public int LineRowHeight {
                get {
                    int r = this.ConsoleMetrics.RowsInRegion(0, lineIndex);
                    if (isAtLastLine)
                        r += this.ConsoleMetrics.ExtraEditorRows;
                    return r;
                }
            }
            public string Text {
                get {
                    string line = this.Line;
                    int st = Math.Min(this.CharacterOffset, line.Length);
                    return line.Substring(st, Math.Min(this.ConsoleMetrics.Columns, line.Length - st));
                }
            }

            public IEnumerable<RowInfo> IterateFrom()
            {
                RowInfo curRow = this;
                int curLineHeight = this.LineRowHeight;

                while (true)
                {
                    yield return curRow;

                    if (curRow.rowSubindexInLine + 1 == curLineHeight)
                    {
                        //go to next line
                        int nextLine = curRow.lineIndex + 1;
                        curRow = new RowInfo()
                        {
                            ConsoleMetrics = this.ConsoleMetrics,
                            rowIndex = curRow.rowIndex + 1,
                            rowSubindexInLine = 0,
                            lineIndex = nextLine,
                            isAtLastLine = (nextLine == this.ConsoleMetrics.lastLineIndex)
                        };
                        if (curRow.lineIndex == this.ConsoleMetrics.metrics[0].Count)
                            yield break; //there is no next line, so exit
                        curLineHeight = curRow.LineRowHeight;
                    }
                    else
                    {
                        //iterate within line
                        curRow = new RowInfo()
                        {
                            ConsoleMetrics = this.ConsoleMetrics,
                            rowIndex = curRow.rowIndex + 1,
                            rowSubindexInLine = curRow.rowSubindexInLine + 1,
                            lineIndex = curRow.lineIndex,
                            isAtLastLine = curRow.isAtLastLine
                        };
                    }
                }
            }
        }

        public RowInfo? FindRow(int rowIndex)
        {
            if (rowIndex < 0)
                throw new System.ArgumentOutOfRangeException();
            int tier = metrics.Count - 1;
            int region = 0;
            int curRow = 0;
            bool found = false;
            while (!found)
            {
                if (region == metrics[tier].Count)
                    return null;
                int rowOfNextRegion = curRow + RowsInRegion(tier, region);
                if (rowIndex < rowOfNextRegion)
                {
                    if (tier == 0)
                        found = true;
                    else
                    {
                        tier--;
                        region = region << listScalingBits;
                    }
                }
                else
                {
                    region++;
                    curRow = rowOfNextRegion;
                }
            }

            return new RowInfo()
            {
                ConsoleMetrics = this,
                rowIndex = rowIndex,
                rowSubindexInLine = rowIndex - curRow,
                lineIndex = region,
                isAtLastLine = (region == lastLineIndex)
            };
        }
    }

    internal class ConsoleTextureCacheRenderer
    {
        // Font
        private static readonly int fontsize = 0;
        private static Font _unityfont = null;
        public static Font UnityFont
        {
            get
            {
                if (_unityfont == null)
                {
                    string path = Util.ResourcePath("font");
                    //this was helpful: https://stackoverflow.com/a/50687018
                    AssetBundle assetBundle = AssetBundle.LoadFromFile(path);
                    if (assetBundle == null)
                        throw new System.IO.FileNotFoundException("Can't find/invalid archive at: " + path);
                    _unityfont = assetBundle.LoadAsset<Font>("Assets/cour.ttf");
                    _unityfont.RequestCharactersInTexture("M");
                }
                return _unityfont;
            }
        }
        public static UnityEngine.CharacterInfo M
        {
            get
            {
                UnityEngine.CharacterInfo r;
                UnityFont.GetCharacterInfo('M', out r, fontsize);
                return r;
            }
        }

        // Cache
        private static readonly int renderTextureSize = 256;
        private static readonly int renderTextureMaxCacheSize = 60;

        private class Render
        {
            public string text;
            public Texture2D tex;
            public HashSet<IntVec2> positions;

            public Render() { }
            public Render(Render other)
            {
                text = other.text;
                tex = new Texture2D(renderTextureSize, renderTextureSize);
                Graphics.CopyTexture(other.tex, tex);
                positions = new HashSet<IntVec2>(other.positions);
            }
        }

        private Render blankRender;
        private LinkedList<Render> rendersChronologically = new LinkedList<Render>();
        private Dictionary<string, LinkedListNode<Render>> rendersByContent =
            new Dictionary<string, LinkedListNode<Render>>();
        private Dictionary<IntVec2, LinkedListNode<Render>> rendersByPosition =
            new Dictionary<IntVec2, LinkedListNode<Render>>();

        // metrics
        private IntVec2 _charGridSize;
        public IntVec2 CharGridSize => _charGridSize;
        public int ContentLengthRequirement => CharGridSize.x * CharGridSize.y;

        // Interface
        public ConsoleTextureCacheRenderer()
        {
            _charGridSize = new IntVec2()
            {
                x = renderTextureSize / M.glyphWidth,
                y = renderTextureSize / M.glyphHeight
            };
            blankRender = new Render()
            {
                text = (new StringBuilder().Append(' ', ContentLengthRequirement)).ToString(),
                tex = new Texture2D(renderTextureSize, renderTextureSize),
                positions = new HashSet<IntVec2>()
            };
            var black = new Color(0, 0, 0);
            blankRender.tex.SetPixels(
                Enumerable.Range(0, renderTextureSize * renderTextureSize)
                .Select(((x) => black)).ToArray());
        }



        private void UpdateTexture(Texture2D tex, string old_content, string new_content)
        {
            throw new NotImplementedException();
            /*var stupid = new Color(0f, 1f, 0f);
            var eu = Enumerable.Range(0, tex.width * tex.height).Select(x => stupid);
            tex.SetPixels(0, 0, tex.width, tex.height, eu.ToArray());
            for (int i = 0; i < ContentLengthRequirement; i++)
            {
                char new_char = new_content[i];
                if (old_content[i] != new_char)
                {
                    CharacterInfo char_info;
                    UnityFont.GetCharacterInfo(new_char, out char_info);
                    int tex_u = (i % CharGridSize.x) * M.glyphWidth;
                    int tex_v = (i / CharGridSize.x) * M.glyphHeight;
                    int font_u = (int)char_info.uvTopLeft.x;
                    int font_v = (int)char_info.uvTopLeft.y;
                    int u_offset = tex_u - font_u;
                    int v_offset = tex_v - font_v;
                    for (int u = font_u; u < font_u + M.glyphWidth; u++)
                    {
                        for (int v = font_v; v < font_v + M.glyphHeight; v++)
                        {
                            Color colour = ((Texture2D)UnityFont.material.mainTexture).GetPixel(u, v);
                            //todo: process the colour here
                            tex.SetPixel(u + u_offset, v + v_offset, colour);
                        }
                    }
                }
            }
            tex.Apply();
            */
        }

        public Texture2D GetTexture(string content, IntVec2? position=null)
        {
            LinkedListNode<Render> render;
            if (content == blankRender.text)
            {
                return blankRender.tex;
            }
            else if (rendersByContent.TryGetValue(content, out render))
            {
            }
            else if (content == null || content.Length != ContentLengthRequirement)
            {
                throw new System.ArgumentException();
            }
            else
            {
                if (position.HasValue && rendersByPosition.TryGetValue(position.Value, out render))
                {
                    var matchingPositions = render.Value.positions;
                    if (!matchingPositions.Contains(position.Value))
                        throw new System.InvalidOperationException("rendersByPosition and Render objects are not in agreement about the Cache state.");
                    if (matchingPositions.Count > 1)
                    {
                        //split (from other positions)
                        matchingPositions.Remove(position.Value);
                        render = new LinkedListNode<Render>(new Render(render.Value));
                    }
                }
                else if (rendersChronologically.Count < renderTextureMaxCacheSize)
                {
                    render = new LinkedListNode<Render>(new Render(blankRender));
                }
                else
                {
                    render = rendersChronologically.First;
                    foreach (IntVec2 pos in render.Value.positions)
                        rendersByPosition.Remove(pos);
                }

                render.Value.positions.Clear();
                var old_content = render.Value.text;
                rendersByContent.Remove(old_content);
                rendersByContent[content] = render;
                UpdateTexture(render.Value.tex, old_content, content);
            }

            if (position.HasValue)
            {
                render.Value.positions.Add(position.Value);
                rendersByPosition[position.Value] = render;
            }

            render.List?.Remove(render);
            rendersChronologically.AddLast(render);
            return render.Value.tex;
        }
    }

    public class ConsoleWindow : Window
    {
        private Console console;
        private ConsoleMetrics metrics;
        //private ConsoleTextureCacheRenderer textureCacheRenderer;
        private Texture2D bgTex;
        private int controlIDHint;
        float titlebarHeight = 28f;
        float statusbarHeight = 22f;
        float consoleMargin = 4f;
        private bool hasSetInitialRectOnce = false;

        public void DrawConsole(Rect rect)
        {
            GUI.DrawTexture(rect, bgTex);

            metrics.Columns = (int)rect.width / ConsoleTextureCacheRenderer.M.glyphWidth;
            int line_height = ConsoleTextureCacheRenderer.M.glyphHeight;
            int line_offset = 0;
            var first_row = metrics.FindRow(0);
            var style = new GUIStyle();
            style.font = ConsoleTextureCacheRenderer.UnityFont;

            // setting color doesn't work???:
            // except now it does??
            // todo: figure out which one of these can change the colour
            // ALSO: https://docs.unity3d.com/ScriptReference/GUIStyle.html
            // add styles to make the characters line up properly with where they're expected
            Color color = new Color(0.9f, 0.9f, 1f);
            style.normal.textColor = color;
            GUI.color = color;

            if (first_row.HasValue)
            {
                //Log.Message("totalrows=" + metrics.TotalRows() + " totallines=" + console.output.Count().ToString() + " extrarows=" + metrics.ExtraEditorRows);
                foreach (var row in first_row.Value.IterateFrom())
                {
                    //Log.Message("row=" + row.rowIndex.ToString() + " line="+row.lineIndex + " charoffset=" + row.CharacterOffset);
                    GUI.Label(new Rect(rect.x, rect.y + line_offset, rect.width, line_height), row.Text, style);
                    line_offset += line_height;
                }
            }
            /*DrawConsole()
            int len = textureCacheRenderer.ContentLengthRequirement;
            var str = new StringBuilder("abcd");
            str.Append(' ', len - 4);
            //Log.Message("DrawConsole");
            var tex = textureCacheRenderer.GetTexture(str.ToString());
            GUI.DrawTexture(new Rect(rect.x, rect.y, tex.width, tex.height), tex);
            */
        }

        protected override float Margin => 1f;

        public override void DoWindowContents(Rect borderRect)
        {
            Rect consoleRect = new Rect(borderRect);
            consoleRect.yMin += titlebarHeight;
            consoleRect.yMax -= statusbarHeight;
            consoleRect = consoleRect.ContractedBy(consoleMargin);

            {
                var button_rects = Enumerable.Range(0, int.MaxValue).Select(delegate (int x) {
                    float right = borderRect.width - 32f - (32f * x);
                    return new Rect(right - 24f, 3f, 24f, 24f);
                }).GetEnumerator();

                //new window button
                button_rects.MoveNext();
                if (Widgets.ButtonImage(button_rects.Current, ConsoleTexturePool.Get("Console/NewConsoleWindowButton")))
                {
                    //button pressed
                    Find.WindowStack.Add(new ConsoleWindow());
                }
            }

            int id = GUIUtility.GetControlID(controlIDHint, FocusType.Keyboard, consoleRect);

            switch (Event.current.type)
            {
                case EventType.Repaint:
                    DrawConsole(consoleRect);
                    break;
                case EventType.MouseDown:
                    GUIUtility.keyboardControl = id;
                    break;
                case EventType.KeyDown:
                    if (GUIUtility.keyboardControl != id)
                        break;
                    bool use = true;
                    switch (Event.current.keyCode)
                    {
                        case KeyCode.Return:
                        case KeyCode.KeypadEnter:
                            console.ProcessInput();
                            break;
                        case KeyCode.Delete:
                            console.DeleteChar(false);
                            break;
                        case KeyCode.Backspace:
                            console.DeleteChar(true);
                            break;
                        case KeyCode.LeftArrow:
                            console.EditorCursor--;
                            break;
                        case KeyCode.RightArrow:
                            console.EditorCursor++;
                            break;
                        case KeyCode.UpArrow:
                            console.CurrentInputIndex--;
                            break;
                        case KeyCode.DownArrow:
                            console.CurrentInputIndex++;
                            break;
                        default:
                            char c = Event.current.character;
                            if (ConsoleTextureCacheRenderer.UnityFont.HasCharacter(c))
                            { console.InputChar(c); }
                            else if (c == '\0')
                            { }
                            else
                            { Log.Message(((int)Event.current.character).ToString()); use = false; }
                            break;
                    }
                    if (use)
                        Event.current.Use();
                    break;
            }
        }

        public override Vector2 InitialSize => new Vector2(550, 350);

        protected override void SetInitialSizeAndPosition()
        {
            if (!hasSetInitialRectOnce
                || windowRect.xMin < 0
                || windowRect.yMin < 0
                || windowRect.xMax > UI.screenWidth
                || windowRect.yMax > UI.screenHeight)
            {
                base.SetInitialSizeAndPosition();
                hasSetInitialRectOnce = true;
            }
        }

        public ConsoleWindow(Console attachConsole = null)
        {
            //Verse.Window config
            resizeable = true;
            draggable = true;
            preventCameraMotion = false;
            doCloseX = true;
            optionalTitle = ""; //should render this myself, to avoid the title causing side effects in Window.WindowOnGUI
            closeOnAccept = false;
            closeOnCancel = false;
            onlyOneOfTypeAllowed = false;

            //the rest
            console = attachConsole ?? new Console();
            metrics = new ConsoleMetrics(console, 10);
            //textureCacheRenderer = new ConsoleTextureCacheRenderer();
            bgTex = new Texture2D(1, 1);
            bgTex.SetPixel(0, 0, new Color(0f, 0f, 0.5f));
            bgTex.Apply();
            controlIDHint = Util.Random.Next();
            //bgTex = (Texture2D)font.material.mainTexture;
        }
    }

    public class ConsoleButton
    {
        private static HarmonyInstance harmony = null;
        private static ConsoleButton _instance = null;
        public static ConsoleButton Instance
        {
            get
            {
                if (_instance == null)
                    throw new System.InvalidOperationException("ConsoleButton has not been instantiated");
                return _instance;
            }
        }
        public static bool Installed => _instance != null;

        private List<ConsoleWindow> windows_storage = new List<ConsoleWindow>();

        private ConsoleButton() {
        }

        public static void Install()
        {
            if (_instance != null)
                throw new System.InvalidOperationException("A ConsoleButton is already installed");
            _instance = new ConsoleButton();

            if (harmony == null)
                harmony = HarmonyInstance.Create("likeafox.rimworld.python.consolebutton");

            harmony.Patch(typeof(WindowStack).GetMethod("ImmediateWindow",
                BindingFlags.Public | BindingFlags.Instance),
                prefix: new HarmonyMethod(typeof(ConsoleButton).GetMethod(
                    "Harmony_Prefix_WindowStack_ImmediateWindow",
                    BindingFlags.Static | BindingFlags.NonPublic)));
            harmony.Patch(typeof(DebugWindowsOpener).GetMethod("DrawButtons",
                BindingFlags.NonPublic | BindingFlags.Instance),
                transpiler: new HarmonyMethod(typeof(ConsoleButton).GetMethod(
                    "Harmony_Transpiler_DebugWindowsOpener_DrawButtons",
                    BindingFlags.Static | BindingFlags.NonPublic)));
        }

        public static void Uninstall()
        {
            throw new NotImplementedException();
        }

        private static void ButtonOnGUI(WidgetRow widgetRow)
        {
            if (widgetRow.ButtonIcon(ConsoleTexturePool.Get("Console/PythonConsoleOpen"), "Open the Python console."))
            {
                //button was clicked
                var inst = Instance;
                List<Verse.Window> windowstack_windows =
                    (List<Window>)typeof(WindowStack).InvokeMember("windows", BindingFlags.GetField
                    | BindingFlags.Instance | BindingFlags.NonPublic, null, Find.WindowStack, null);
                List<ConsoleWindow> visible_windows =
                    windowstack_windows.FindAll(w => typeof(ConsoleWindow).IsInstanceOfType(w))
                    .Cast<ConsoleWindow>().ToList();

                if (visible_windows.Count > 0)
                {
                    //put all visible console windows into storage
                    foreach (Window w in visible_windows)
                        Find.WindowStack.TryRemove(w, false);
                    inst.windows_storage.AddRange(visible_windows);
                }
                else
                {
                    if (inst.windows_storage.Count > 0)
                    {
                        //move stored windows back to WindowStack
                        foreach (Window w in inst.windows_storage)
                            Find.WindowStack.Add(w);
                        inst.windows_storage.Clear();
                    }
                    else
                    {
                        //there are no console windows at all; make one
                        Find.WindowStack.Add(new ConsoleWindow());
                    }
                }
            }
        }

        private static void Harmony_Prefix_WindowStack_ImmediateWindow(int ID, ref Rect rect)
        {
            if (ID == 1593759361)
            {
                rect.width += 28f;
            }
        }

        private static IEnumerable<CodeInstruction>
            Harmony_Transpiler_DebugWindowsOpener_DrawButtons(IEnumerable<CodeInstruction> instructions)
        {
            CodeInstruction[] first_two = instructions.Take(2).ToArray();
            yield return first_two[0];
            yield return first_two[1];

            //https://en.wikipedia.org/wiki/List_of_CIL_instructions
            yield return new CodeInstruction(OpCodes.Ldloc_0);
            var method = typeof(ConsoleButton).GetMethod("ButtonOnGUI",
                BindingFlags.Static | BindingFlags.NonPublic);
            yield return new CodeInstruction(OpCodes.Call, method);

            IEnumerable<CodeInstruction> the_rest = instructions.Skip(2);
            foreach (var ci in the_rest)
                yield return ci;
        }
    }

    internal static class ConsoleTexturePool
    {
        private static Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();

        internal static Texture2D Get(string path)
        {
            try
            {
                return textures[path];
            }
            catch { }
            return textures[path] = ContentFinder<Texture2D>.Get(path, true);
        }
    }
}
