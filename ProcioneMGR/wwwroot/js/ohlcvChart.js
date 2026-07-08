// Interop per TradingView Lightweight Charts v5.
// Supporta candlestick + volume + serie multiple sovrapposte (Line/Histogram),
// su scala prezzi principale ("price") o su un riquadro inferiore ("osc").

const charts = new Map();

export function init(container, id, showCandles) {
    const LWC = window.LightweightCharts;
    if (!LWC) {
        console.error('[ohlcvChart] LightweightCharts non caricato (CDN).');
        return;
    }

    const chart = LWC.createChart(container, {
        layout: { background: { color: 'transparent' }, textColor: '#333' },
        grid: {
            vertLines: { color: 'rgba(197, 203, 206, 0.3)' },
            horzLines: { color: 'rgba(197, 203, 206, 0.3)' },
        },
        crosshair: { mode: LWC.CrosshairMode.Normal },
        timeScale: { timeVisible: true, secondsVisible: false, borderColor: 'rgba(197, 203, 206, 0.8)' },
        rightPriceScale: { borderColor: 'rgba(197, 203, 206, 0.8)' },
        autoSize: true,
    });

    let candleSeries = null;
    let volumeSeries = null;

    if (showCandles !== false) {
        candleSeries = chart.addSeries(LWC.CandlestickSeries, {
            upColor: '#26a69a', downColor: '#ef5350', borderVisible: false,
            wickUpColor: '#26a69a', wickDownColor: '#ef5350',
        });
        volumeSeries = chart.addSeries(LWC.HistogramSeries, { priceFormat: { type: 'volume' }, priceScaleId: '' });
        volumeSeries.priceScale().applyOptions({ scaleMargins: { top: 0.8, bottom: 0 } });
    }

    charts.set(id, { chart, candleSeries, volumeSeries, indicatorSeries: [] });
}

export function setData(id, candles) {
    const entry = charts.get(id);
    if (!entry || !entry.candleSeries) return;

    entry.candleSeries.setData(candles.map(c => ({
        time: c.time, open: c.open, high: c.high, low: c.low, close: c.close,
    })));
    if (entry.volumeSeries) {
        entry.volumeSeries.setData(candles.map(c => ({
            time: c.time, value: c.volume,
            color: c.close >= c.open ? 'rgba(38, 166, 154, 0.5)' : 'rgba(239, 83, 80, 0.5)',
        })));
    }
    entry.chart.timeScale().fitContent();
}

// series: [{ title, color, type: 'Line'|'Histogram', scale: 'price'|'osc', data: [{time, value}] }]
export function setIndicators(id, series) {
    const entry = charts.get(id);
    if (!entry) return;
    const LWC = window.LightweightCharts;

    // Rimuovi le serie indicatore precedenti.
    for (const s of entry.indicatorSeries) {
        entry.chart.removeSeries(s);
    }
    entry.indicatorSeries = [];

    let oscConfigured = false;
    for (const def of (series || [])) {
        const onOsc = def.scale === 'osc';
        const opts = { color: def.color, title: def.title, lastValueVisible: true, priceLineVisible: false };
        if (onOsc) opts.priceScaleId = 'osc';

        const type = def.type === 'Histogram' ? LWC.HistogramSeries : LWC.LineSeries;
        if (def.type !== 'Histogram') opts.lineWidth = 2;

        const s = entry.chart.addSeries(type, opts);
        s.setData(def.data);
        entry.indicatorSeries.push(s);

        if (onOsc && !oscConfigured) {
            s.priceScale().applyOptions({ scaleMargins: { top: 0.75, bottom: 0 } });
            oscConfigured = true;
        }
    }

    entry.chart.timeScale().fitContent();
}

export function dispose(id) {
    const entry = charts.get(id);
    if (entry) {
        entry.chart.remove();
        charts.delete(id);
    }
}
