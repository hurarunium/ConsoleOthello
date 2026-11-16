// 計測ページ(Phase 1)。AIは Web Worker の中で動かし、画面が固まらないことも確かめる
const log = (text) => { document.getElementById('log').textContent += text + '\n'; };
let worker = null;
let pending = null;

// 画面が固まっていないかを見るための時計(AIが考えている間も数が増えれば、画面は動いている)
let ticks = 0;
setInterval(() => { document.getElementById('clock').textContent = String(++ticks); }, 100);

function call(message) {
  return new Promise((resolve, reject) => {
    pending = { resolve, reject };
    worker.postMessage(message);
  });
}

function onWorkerMessage(event) {
  const message = event.data;
  if (message.type === 'error') pending?.reject(new Error(message.message));
  else pending?.resolve(message);
  pending = null;
}

// 読み込んだファイルの大きさ(Resource Timing。transferSize は実際に送られた量、decodedBodySize は展開後)
function transferSummary() {
  const entries = performance.getEntriesByType('resource');
  const sum = (key) => entries.reduce((total, entry) => total + (entry[key] || 0), 0);
  return `${entries.length}ファイル 転送 ${(sum('transferSize') / 1e6).toFixed(2)}MB 展開後 ${(sum('decodedBodySize') / 1e6).toFixed(2)}MB`;
}

document.getElementById('init').addEventListener('click', async () => {
  const ttBits = Number(document.getElementById('ttBits').value);
  worker?.terminate();
  worker = new Worker('worker.js', { type: 'module' });
  worker.onmessage = onWorkerMessage;
  const started = performance.now();
  try {
    const ready = await call({ type: 'init', ttBits });
    log(`読み込み完了: 合計 ${Math.round(performance.now() - started)}ms(.NET起動 ${ready.runtimeMs}ms・評価ファイル取得と展開 ${ready.fetchMs}ms・C#での読み込み ${ready.loadMs}ms) 置換表 2^${ttBits}`);
    log(`メインのページの読み込み: ${transferSummary()}`);
    log(`Worker の読み込み: ${ready.resources}`);
    document.getElementById('run').disabled = false;
  } catch (error) {
    log(`読み込み失敗: ${error.message}`);
  }
});

document.getElementById('run').addEventListener('click', async () => {
  const transcript = document.getElementById('transcript').value.trim();
  const levels = document.getElementById('levels').value.split(',').map(Number).filter(Boolean);
  for (const level of levels) {
    try {
      const result = await call({ type: 'measure', transcript, level });
      log(`レベル${level}: ${result.count}局面 合計 ${result.totalMs}ms 平均 ${Math.round(result.totalMs / result.count)}ms 最大 ${result.maxMs}ms(${result.maxMove}手目)`);
      log(`  手: ${result.moves.join(' ')}`);
      log(`  局面数: ${result.nodes.join(' ')}`);
    } catch (error) {
      log(`レベル${level}: 失敗 ${error.message}`);
    }
  }
  log('計測おわり');
});
