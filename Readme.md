# UnityToNiconicoConverter
---
Unityで出力したWebGL向けゲームをニコ生ゲームでプレイできるように変換するツールです。

## ビルド
Releaseにビルド済み実行ファイルもありますが、ビルドして使うこともできます。

UnityToNiconicoConverter.slnを開いてビルドするだけです。

参照パッケージに[Esprima .NET](https://github.com/sebastienros/esprima-dotnet)を使用しています。

## 使い方
```
UnityToNiconicoConverter.exe "UnityOutputフォルダ" "ニコ生ゲームフォルダ"
```
UnityOutputフォルダはBuild.loader.jsファイルなどが含まれるフォルダです。Unityから書き出す際のフォルダ名は必ずBuildにしてください。

ニコ生ゲームフォルダはgame.jsonが置かれているフォルダです。

上記コマンドを実行するとニコ生ゲームフォルダのscriptフォルダとbinaryフォルダ以下に変換されたjsやgzファイルが生成されます。
あとはそれらをgame.jsonに追加し、以下のようにloaderスクリプトを起点に読み込めば起動するはずです。
binaryフォルダ以下のファイルはtextアセットとして追加してください。
```javascript
const loader = require('./Build.loader');

exports.main = void 0;
function main(param) {
    var scene = new g.Scene({
        game: g.game
    });
    scene.onLoad.add(function () {
        // Unity側に渡すコンフィグデータを作成
        var config = {
            dataUrl: g.game._assetManager.configuration['Build.data'].path,
            framework: function () {
                return require('./Build.framework')(this);
            },
            codeUrl: g.game._assetManager.configuration['Build.wasm'].path,
            streamingAssetsUrl: "StreamingAssets",
            companyName: "作者の名前",
            productName: "ゲームの名前",
            productVersion: "1.0.0",
            matchWebGLToCanvasSize: false,
            showBanner: () => { }
        };

        const canvas = document.createElement('canvas');
        // ID名は何でもいいが空文字だとunity側でquerySelector出来なくなるので適当に付けておく
        canvas.id = 'unity-canvas';
        canvas.width = g.game.width;
        canvas.height = g.game.height;
        // idついてないキャンバスがAkashicゲーム表示用のはず
        const mainCanvas = Array.prototype.find.call(document.querySelectorAll('canvas'), e => {
            return e.id.length == 0;
        });
        mainCanvas.parentElement.insertBefore(canvas, mainCanvas);

        // キャンバスのスタイル変更ハンドラ
        const canvasLocation = new EventTarget();
        const onWindowResize = () => canvasLocation.dispatchEvent(new Event('changed'));
        new MutationObserver(onWindowResize).observe(mainCanvas, {
            attributes: true,
            attributeFilter: ['style'],
        });

        // メインキャンバスのスタイルをUnityキャンバスにコピー
        const resized = () => {
            for (const attr of mainCanvas.style) {
                canvas.style[attr] = mainCanvas.style[attr];
            }
        };

        canvasLocation.addEventListener('changed', resized);
        resized();

        // ゲームのロード開始
        loader(canvas, config, () => { }).then(instance => {
        });
    });
    g.game.pushScene(scene);
}
exports.main = main;
```

動作確認バージョン:Unity 2022.3.8f1
## Unityの出力オプション
Unty側のビルド設定はほぼデフォルトで問題ないですが、一部注意点があるのでまとめておきます。
### 解像度と表示タブ
* キャンバスサイズ
  * game.jsonのサイズに合わせる
* WebGLテンプレート
  * どれでもいい気はするけどMinimalが無難そう
### その他の設定タブ
* テクスチャ形式
  * スマートフォンでのパフォーマンスを重視してASTCが良いと思う
* ライトマップストリーミング
  * 関係ないかもしれないけどOFFにしておくのが良さそう
### 公開設定タブ
* 圧縮形式
  * Gzipのみ対応
* データキャッシング
  * 使えるかもしれないけどOFFが無難そう

## ライセンス
特にありません。

## 免責事項
ユーザーが本プログラムを使用したことにより生じた損害等に対して作者は如何なる責任も負わないものとします。
