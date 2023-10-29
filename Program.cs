using Esprima;
using Esprima.Ast;
using Esprima.Utils;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace UnityToNiconicoConverter
{
    internal class Program
    {
        static void Main(string[] args)
        {
            // 引数チェック
            var (loaderPath, frameworkPath, dataPath, wasmPath, nicoDir) = args.Length != 2
                || !Directory.Exists(args[0])
                || !Directory.Exists(args[1]) ? (null, null, null, null, null) : (
                Path.Combine(args[0], "Build.loader.js"),
                Path.Combine(args[0], "Build.framework.js.gz"),
                Path.Combine(args[0], "Build.data.gz"),
                Path.Combine(args[0], "Build.wasm.gz"),
                args[1]
                );
            if (!File.Exists(loaderPath)
                || !File.Exists(frameworkPath)
                || !File.Exists(dataPath)
                || !File.Exists(wasmPath)
                || nicoDir == null)
            {
                Console.WriteLine(@$"使い方:
{AppDomain.CurrentDomain.FriendlyName} ""Build.loader.jsが出力されたフォルダパス"" ""ニコ生ゲームのフォルダパス""");
                return;
            }

            // javascript読み込み
            using var gz = File.Open(frameworkPath, FileMode.Open);
            using var gzs = new GZipStream(gz, CompressionMode.Decompress);
            var utf8 = new List<byte>();
            var buf = new byte[4096];
            int len;
            while ((len = gzs.Read(buf)) != 0) utf8.AddRange(len == buf.Length ? buf : buf.Take(len));
            var frameworkJs = Encoding.UTF8.GetString(utf8.ToArray());
            var loaderJs = File.ReadAllText(loaderPath, Encoding.UTF8);

            // 出力先作成
            var scriptDir = Path.Combine(nicoDir, "script");
            if (!Directory.Exists(scriptDir))
            {
                Directory.CreateDirectory(scriptDir);
            }
            var binaryDir = Path.Combine(nicoDir, "binary");
            if (!Directory.Exists(binaryDir))
            {
                Directory.CreateDirectory(binaryDir);
            }

            // スクリプト改変
            ReplaceLoader(loaderJs, scriptDir, loaderPath);
            ReplaceFramework(frameworkJs, scriptDir, Path.GetFileNameWithoutExtension(frameworkPath));

            // データ類コピー
            File.Copy(dataPath, Path.Combine(binaryDir, Path.GetFileName(dataPath)), true);
            File.Copy(wasmPath, Path.Combine(binaryDir, Path.GetFileName(wasmPath)), true);
        }

        static void ReplaceLoader(string js, string outDir, string path)
        {
            var ast = new JavaScriptParser(ParserOptions.Default).ParseScript(js);
            var replacer = new Replacer();
            // スクリプト読み込み関数を丸っと置換
            replacer.AddTask(new ReplaceTask(
                new TypeMatch(Nodes.BlockStatement) & new AnyChild(
                        new TypeMatch(Nodes.VariableDeclaration) & new AnyChild(
                            new TypeMatch(Nodes.VariableDeclarator) & new AnyChild(
                            new TypeMatch(Nodes.CallExpression) & new AnyChild(
                                new TypeMatch(Nodes.MemberExpression) & new PropertyCond("Object", new Identity("document")) & new PropertyCond("Property", new Identity("createElement"))
                                ) & new AnyChild(
                                    new StringLiteral("script")
                                    )
                            )
                            )
                        ),
                n =>
                {
                    var found = (BlockStatement)n;
                    return found.UpdateWith(NodeList.Create(new Statement[] {
                        new ExpressionStatement(
                            new CallExpression(
                                new Identifier("a"),
                                NodeList.Create(
                                    new Expression[]{
                                        new CallExpression(
                                            new StaticMemberExpression(
                                                new Identifier("c"),
                                                new Identifier("frameworkUrl"),
                                                false),
                                            EmptyList<Expression>(),
                                            false)
                                    }),
                                false)
                            )
                    }));
                }));

            // 取得コードの展開処理を挿入
            replacer.AddTask(new ReplaceTask(
                new TypeMatch(Nodes.SequenceExpression) & new AnyChild(
                new TypeMatch(Nodes.CallExpression) & new AnyChild(
                    new TypeMatch(Nodes.MemberExpression) &
                    new PropertyCond("Property", new Identity("addRunDependency"))
                ) &
                    new PropertyCond<NodeList<Expression>>("Arguments", new AnyNodes<Expression>(new StringLiteral("dataUrl")))
                ),
                n =>
                {
                    var target = (SequenceExpression)n;
                    return target.UpdateWith(NodeList.Create(target.Expressions.Select(seq =>
                    {
                        if (seq.Type == Nodes.CallExpression && seq.As<CallExpression>().Callee.Type == Nodes.MemberExpression && (seq.As<CallExpression>().Callee.As<MemberExpression>().Property as Identifier)?.Name == "then")
                        {
                            var replaced = new JavaScriptParser(ParserOptions.Default).ParseExpression(seq.As<CallExpression>().Callee.ToJavaScriptString() + @"
(buf => new Response(new Response(buf).body.pipeThrough(new DecompressionStream(""gzip""))).arrayBuffer())
.then(buf => new Uint8Array(buf))
");
                            var call = seq.As<CallExpression>();
                            var callee = call.Callee.As<MemberExpression>();
                            return call.UpdateWith(callee.UpdateWith(replaced, callee.Property), call.Arguments);
                        }

                        return seq;
                    })));
                }));

            // プログラムの前後に変数宣言とモジュールエクスポート文を付け加える
            replacer.AddTask(new ReplaceTask(
                new TypeMatch(Nodes.Program),
                n =>
                {
                    var program = (Esprima.Ast.Program)n;

                    var newStatement = new List<Statement>
                    {
                            // プログラム先頭に変数宣言追加
                            new VariableDeclaration(NodeList.Create(
                                new VariableDeclarator[] {
                                    new VariableDeclarator(new Identifier("gl"), null),
                                    new VariableDeclarator(new Identifier("glVersion"), null)
                                }),
                                VariableDeclarationKind.Var)
                    };
                    newStatement.AddRange(program.Body);
                    newStatement.Add(new ExpressionStatement(
                        new AssignmentExpression(
                            AssignmentOperator.Assign,
                            new StaticMemberExpression(
                                new Identifier("module"),
                                new Identifier("exports"),
                                false),
                            new Identifier("createUnityInstance")
                            )
                        )
                        );
                    return program.UpdateWith(NodeList.Create(newStatement));
                }, ReplaceTask.Phase.Post));
            ast = (Script)replacer.Visit(ast)!;
            var reconstructed = ast.ToJavaScriptString();
            File.WriteAllText(Path.Combine(outDir, Path.GetFileName(path)), reconstructed, new UTF8Encoding(false));
        }

        static void ReplaceFramework(string js, string outDir, string path)
        {
            var ast = new JavaScriptParser(ParserOptions.Default).ParseScript(js);
            var replacer = new Replacer();

            // エクスポート関数の引数にコンフィグを渡せるようにする
            replacer.AddTask(new ReplaceTask(
                new TypeMatch(Nodes.ArrowFunctionExpression),
                n =>
                {
                    var target = (ArrowFunctionExpression)n;

                    return target.UpdateWith(NodeList.Create(new Node[] { new Identifier("config") }), target.Body);
                }));

            // プログラムの最初に自動require防止コードを挿入する
            replacer.AddTask(new ReplaceTask(
                new TypeMatch(Nodes.Program),
                n =>
                {
                    var program = (Esprima.Ast.Program)n;

                    var newStatement = new List<Statement>
                    {
                            // プログラム先頭に定数宣言追加
                            new VariableDeclaration(NodeList.Create(
                                new VariableDeclarator[] {
                                    new VariableDeclarator(new Identifier("process"), new Identifier("undefined")),
                                    new VariableDeclarator(new Identifier("setImmediate"), new Identifier("undefined"))
                                }),
                                VariableDeclarationKind.Const)
                    };
                    newStatement.AddRange(program.Body);
                    return program.UpdateWith(NodeList.Create(newStatement));
                }, ReplaceTask.Phase.Post));

            // WEBAudioにマスターボリューム変数を追加
            replacer.AddTask(new ReplaceTask(
                new TypeMatch(Nodes.VariableDeclarator) & new PropertyCond("Id", new Identity("WEBAudio")),
                n =>
                {
                    var target = (VariableDeclarator)n;

                    var exp = target.Init?.As<ObjectExpression>()!;
                    return target.UpdateWith(target.Id, exp.UpdateWith(NodeList.Create(exp.Properties.Concat(new Node[] { new Property(PropertyKind.Init, new Identifier("masterVolume"), false, new Literal("null"), false, false) }))));
                }, ReplaceTask.Phase.Post));

            // マスターボリュームの初期化追加
            replacer.AddTask(new ReplaceTask(
                new TypeMatch(Nodes.FunctionDeclaration) & new PropertyCond("Id", new Identity("_JS_Sound_Init")),
                n =>
                {
                    var target = (FunctionDeclaration)n;

                    var body = target.Body;
                    var @try = body.Body[0].As<TryStatement>();// ここはtry文のみの前提で考えちゃう

                    var initializes = @try.Block.Body.ToList();

                    // 初期化の末尾に追加
                    var WEBAudio = new Identifier("WEBAudio");
                    var masterVolume = new StaticMemberExpression(WEBAudio, new Identifier("masterVolume"), false);
                    var audioContext = new StaticMemberExpression(WEBAudio, new Identifier("audioContext"), false);
                    initializes.Add(new ExpressionStatement(
                        new AssignmentExpression(
                            AssignmentOperator.Assign,
                            masterVolume,
                            new CallExpression(
                                new StaticMemberExpression(
                                    audioContext,
                                    new Identifier("createGain"),
                                    false),
                                EmptyList<Expression>(),
                                false)
                            )
                        )
                        );

                    initializes.Add(new ExpressionStatement(
                        new CallExpression(
                            new StaticMemberExpression(
                                masterVolume,
                                new Identifier("connect"),
                                false),
                            NodeList.Create(new Expression[] {
                                    new StaticMemberExpression(audioContext, new Identifier("destination"), false)
                            }),
                            false)
                        )
                        );

                    var audioManager = new StaticMemberExpression(new Identifier("config"), new Identifier("audioManager"), false);
                    var setMasterVolume = new Identifier("setMasterVolume");
                    initializes.Add(new VariableDeclaration(
                        NodeList.Create(new VariableDeclarator[]{
                                new VariableDeclarator(setMasterVolume, new ArrowFunctionExpression(
                                    EmptyList<Node>(),
                                    new BlockStatement(NodeList.Create(new Statement[]{
                                        new ExpressionStatement(
                                            new AssignmentExpression(
                                                AssignmentOperator.Assign,
                                                new StaticMemberExpression(
                                                    new StaticMemberExpression(masterVolume, new Identifier("gain"),false),
                                                    new Identifier("value"),false),
                                                new CallExpression(
                                                    new StaticMemberExpression(audioManager, new Identifier("getMasterVolume"),false),
                                                    EmptyList<Expression>(),
                                                    false)
                                                )
                                            ) })),
                                            false,
                                            false,
                                            false)
                                )
                        }), VariableDeclarationKind.Const
                    ));

                    initializes.Add(new ExpressionStatement(
                        new CallExpression(
                            setMasterVolume,
                            EmptyList<Expression>(),
                            false)
                        )
                        );

                    initializes.Add(new ExpressionStatement(
                        new CallExpression(
                            new StaticMemberExpression(
                                audioManager,
                                new Identifier("registerAudioAsset"),
                                false),
                            NodeList.Create(new Expression[] {
                                    new ObjectExpression(NodeList.Create(new Node[]{
                                        new Property(
                                            PropertyKind.Init,
                                            new Identifier("_lastPlayedPlayer"),
                                            false,
                                            new ObjectExpression(NodeList.Create(new Node[]{
                                                new Property(
                                                    PropertyKind.Init,
                                                    new Identifier("notifyMasterVolumeChanged"),
                                                    false,
                                                    setMasterVolume,
                                                    false,
                                                    false)
                                                })),
                                                false,
                                                false)
                                            })
                                        )
                                }),
                                false)
                            )
                        );

                    var block = @try.Block.UpdateWith(NodeList.Create(initializes));

                    body = body.UpdateWith(NodeList.Create(new Statement[] { @try.UpdateWith(block, @try.Handler, @try.Finalizer) }));

                    return target.UpdateWith(target.Id, target.Params, body);
                }, ReplaceTask.Phase.Post));

            // オーディオノードの出力先をマスターボリュームに変更
            replacer.AddTask(new ReplaceTask(
                new TypeMatch(Nodes.MemberExpression) & new PropertyCond("Property", new Identity("destination")),
                n =>
                {
                    var target = (MemberExpression)n;
                    return target.UpdateWith(new Identifier("WEBAudio"), new Identifier("masterVolume"));
                }, ReplaceTask.Phase.Post));

            ast = (Script)replacer.Visit(ast)!;

            // エクスポートをオブジェクトではなく関数にする
            replacer.AddTask(new ReplaceTask(
                new TypeMatch(Nodes.VariableDeclarator) & new PropertyCond("Id", new Identity("unityFramework")),
                n =>
                {
                    var target = (VariableDeclarator)n;
                    return target.UpdateWith(target.Id, target.Init?.As<CallExpression>().Callee);
                },
                ReplaceTask.Phase.Post));

            // wasmの読み込み処理を独自のものに差し替え
            replacer.AddTask(new ReplaceTask(
                new TypeMatch(Nodes.CallExpression) & new AnyChild(
                    new TypeMatch(Nodes.MemberExpression) & new AnyChild(
                        new TypeMatch(Nodes.CallExpression) & new PropertyCond("Callee", new Identity("fetch"))
                    )
                ) & new AnyDescendant(new Identity("instantiate")),
                n =>
                {
                    var replaced = new JavaScriptParser(ParserOptions.Default).ParseExpression(@"
res => new Response(res.body.pipeThrough(new DecompressionStream(""gzip""))).arrayBuffer()
");

                    var target = (CallExpression)n;

                    var callee = target.Callee.As<MemberExpression>();
                    callee = callee.UpdateWith(
                        new CallExpression(
                            new StaticMemberExpression(
                                callee.Object,
                                new Identifier("then"),
                                false),
                            NodeList.Create(new Expression[] { replaced }),
                            false),
                        callee.Property);

                    return target.UpdateWith(callee, target.Arguments);
                },
                ReplaceTask.Phase.Post));

            // インスタンス化関数をバイナリ版に変更
            replacer.AddTask(new ReplaceTask(
                new TypeMatch(Nodes.CallExpression) & new AnyChild(
                    new TypeMatch(Nodes.MemberExpression) & new PropertyCond("Property", new Identity("instantiateStreaming"))
                    ),
                n =>
                {
                    var target = (CallExpression)n;

                    var callee = target.Callee.As<MemberExpression>();
                    callee = callee.UpdateWith(
                        callee.Object,
                        new Identifier("instantiate"));
                    var arguments = target.Arguments.ToList();
                    arguments[0] = new NewExpression(new Identifier("Uint8Array"), NodeList.Create(arguments.Take(1)));
                    return target.UpdateWith(callee, NodeList.Create(arguments));
                }));

            // 無駄な環境処理を削除
            foreach (var env in new[]{
                    (name: "ENVIRONMENT_IS_NODE", invert: false),
                    (name: "ENVIRONMENT_IS_WORKER", invert: false),
                    (name: "ENVIRONMENT_IS_WEB", invert: true),
                })
            {
                replacer.AddTask(new ReplaceTask(
                    new TypeMatch(Nodes.IfStatement) & new PropertyCond(
                        "Test",
                        new Identity(env.name) |
                        (new TypeMatch(Nodes.UnaryExpression) & new PropertyCond<UnaryOperator>("Operator", new EqualValue<UnaryOperator>(UnaryOperator.LogicalNot))) & new PropertyCond("Argument", new Identity(env.name))
                        )
                    , n =>
                    {
                        var target = (IfStatement)n;

                        if ((target.Test.Type == Nodes.Identifier) ^ env.invert)
                        {
                            return target.Alternate ?? new EmptyStatement();
                        }
                        else
                        {
                            return target.Consequent;
                        }
                    }, once: false
                ));
            }

            ast = (Script)replacer.Visit(ast)!;

            // throw以降のステートメントを削除
            replacer.AddTask(new ReplaceTask(
                new TypeMatch(Nodes.BlockStatement) & new AnyChild(
                    new TypeMatch(Nodes.ThrowStatement) | (new TypeMatch(Nodes.BlockStatement) & new AnyChild(new TypeMatch(Nodes.ThrowStatement)))
                    ),
                n =>
                {
                    var target = (BlockStatement)n;

                    var body = target.Body.Take(target.Body.TakeWhile(e => e.Type switch
                    {
                        Nodes.ThrowStatement => false,
                        Nodes.BlockStatement => !(e.As<BlockStatement>().Body.Count == 1 && e.As<BlockStatement>().Body[0].Type == Nodes.ThrowStatement),
                        _ => true
                    }).Count() + 1);
                    return target.UpdateWith(NodeList.Create(body));
                }, once: false));

            ast = (Script)replacer.Visit(ast)!;
            var reconstructed = ast.ToJavaScriptString();
            File.WriteAllText(Path.Combine(outDir, Path.GetFileName(path)), reconstructed, new UTF8Encoding(false));
        }

        #region
        public abstract class Condition
        {
            public abstract bool Satisfy(Node n);

            public static Condition operator &(Condition l, Condition r)
            {
                return new And(l, r);
            }

            public static Condition operator |(Condition l, Condition r)
            {
                return new Any(l, r);
            }
        }

        public abstract class Condition<T>
        {
            public abstract bool Satisfy(T n);
        }

        public class AnyChild : Condition
        {
            Condition cond;

            public AnyChild(Condition c)
            {
                cond = c;
            }

            public override bool Satisfy(Node n)
            {
                return n.ChildNodes.Any(c =>
                {
                    return cond.Satisfy(c);
                });
            }
        }

        public class TypeMatch : Condition
        {
            Nodes type;

            public TypeMatch(Nodes type)
            {
                this.type = type;
            }

            public override bool Satisfy(Node n)
            {
                return n.Type == type;
            }
        }

        public class And : Condition
        {
            Condition[] conditions;

            public And(params Condition[] conditions) { this.conditions = conditions; }

            public And(IEnumerable<Condition> conditions) { this.conditions = conditions.ToArray(); }

            public override bool Satisfy(Node n)
            {
                return conditions.All(c => c.Satisfy(n));
            }
        }

        public class Any : Condition
        {
            Condition[] conditions;

            public Any(params Condition[] conditions) { this.conditions = conditions; }

            public Any(IEnumerable<Condition> conditions) { this.conditions = conditions.ToArray(); }

            public override bool Satisfy(Node n)
            {
                return conditions.Any(c => c.Satisfy(n));
            }
        }

        public class AnyDescendant : Condition
        {
            Condition condition;
            public AnyDescendant(Condition c)
            {
                condition = c;
            }

            public override bool Satisfy(Node n)
            {
                if (condition.Satisfy(n)) return true;
                return n.ChildNodes.Any(Satisfy);
            }
        }

        public class PropertyCond : Condition
        {
            string name;
            Condition condition;
            public PropertyCond(string name, Condition cond)
            {
                this.name = name;
                condition = cond;
            }

            public override bool Satisfy(Node n)
            {
                n = (n.GetType().GetProperty(name)?.GetValue(n) as Node)!;
                return condition.Satisfy(n);
            }
        }

        public class PropertyCond<T> : Condition
        {
            string name;
            Condition<T> condition;
            public PropertyCond(string name, Condition<T> cond)
            {
                this.name = name;
                condition = cond;
            }

            public override bool Satisfy(Node n)
            {
                return condition.Satisfy((T)(n.GetType().GetProperty(name)?.GetValue(n)!));
            }
        }

        public class PropertyCond<V, T> : Condition<V>
        {
            PropertyInfo property;
            Condition<T> condition;
            public PropertyCond(string name, Condition<T> cond)
            {
                property = typeof(V).GetProperty(name)!;
                condition = cond;
                ;
            }

            public override bool Satisfy(V n)
            {
                return condition.Satisfy((T)(property.GetValue(n)!));
            }
        }

        public class EqualValue<T> : Condition<T>
        {
            T value;

            public EqualValue(T val)
            {
                value = val;
            }
            public override bool Satisfy(T n)
            {
                return n!.Equals(value);
            }
        }

        public class Identity : Condition
        {
            string id;

            public Identity(string id)
            {
                this.id = id;
            }

            public override bool Satisfy(Node n)
            {
                return n.Type == Nodes.Identifier && n.As<Identifier>().Name == id;
            }
        }

        public class StringLiteral : Condition
        {
            string str;

            public StringLiteral(string str)
            {
                this.str = str;
            }

            public override bool Satisfy(Node n)
            {
                return n.Type == Nodes.Literal && n.As<Literal>().StringValue == str;
            }
        }

        public class AnyNodes<T> : Condition<NodeList<T>> where T : Node
        {
            Condition condition;

            public AnyNodes(Condition cond)
            {
                condition = cond;
            }
            public override bool Satisfy(NodeList<T> n)
            {
                return n.Any(n => condition.Satisfy(n));
            }
        }

        class Replacer : AstRewriter
        {
            public void AddTask(ReplaceTask task)
            {
                tasks.Add(task);
            }

            public override object? Visit(Node node)
            {
                if (tasks.Count != 0)
                {
                    for (int i = 0; i < tasks.Count; ++i)
                    {
                        if ((tasks[i].RunPhase & ReplaceTask.Phase.Pre) != ReplaceTask.Phase.Pre)
                            continue;
                        var newNode = tasks[i].Run(node);
                        if (newNode != node)
                        {
                            if (tasks[i].OneShot) tasks.RemoveAt(i);
                            return newNode;
                        }
                    }
                    node = (Node)base.Visit(node)!;
                    for (int i = 0; i < tasks.Count; ++i)
                    {
                        if ((tasks[i].RunPhase & ReplaceTask.Phase.Post) != ReplaceTask.Phase.Post)
                            continue;
                        var newNode = tasks[i].Run(node);
                        if (newNode != node)
                        {
                            if (tasks[i].OneShot) tasks.RemoveAt(i);
                            return newNode;
                        }
                    }
                }
                return node;
            }

            private List<ReplaceTask> tasks = new();
        }

        class ReplaceTask
        {
            [Flags]
            public enum Phase
            {
                Pre = 1,
                Post = 2,
                Both = 3
            }
            Condition cond;
            Func<Node, Node> pred;
            Phase phase;
            bool once;  // 単発のタスクかどうか

            public Phase RunPhase { get => phase; }
            public bool OneShot { get => once; }

            public ReplaceTask(Condition cond, Func<Node, Node> pred, Phase phase = Phase.Pre, bool once = true)
            {
                this.cond = cond;
                this.pred = pred;
                this.phase = phase;
                this.once = once;
            }

            public Node Run(Node node)
            {
                if (cond.Satisfy(node))
                {
                    return pred(node);
                }
                return node;
            }
        }

        static NodeList<T> EmptyList<T>() where T : Node?
        {
            return NodeList.Create(Enumerable.Empty<T>());
        }
        #endregion
    }
}