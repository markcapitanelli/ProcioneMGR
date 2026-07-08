// Grafici generici via Plotly (già caricato globalmente via CDN in App.razor).
// Stesso pattern di heatmap.js: modulo ES importato via JS interop dalle pagine Blazor.

function el(id) {
    const e = document.getElementById(id);
    return e && window.Plotly ? e : null;
}

const TRANSPARENT = { paper_bgcolor: 'transparent', plot_bgcolor: 'transparent' };

// Barre orizzontali (es. |IC| per fattore). colors: array opzionale per-barra.
export function barh(elementId, labels, values, xTitle, colors) {
    const e = el(elementId);
    if (!e) return;
    const data = [{
        type: 'bar',
        orientation: 'h',
        x: values,
        y: labels,
        marker: { color: colors || '#2962FF' },
        hovertemplate: `%{y}: %{x:.4f}<extra></extra>`,
    }];
    const layout = {
        margin: { l: 170, r: 20, t: 10, b: 40 },
        xaxis: { title: xTitle, zeroline: true },
        yaxis: { automargin: true, autorange: 'reversed' },
        height: Math.max(220, labels.length * 26 + 70),
        ...TRANSPARENT,
    };
    window.Plotly.react(e, data, layout, { displayModeBar: false, responsive: true });
}

// Barre verticali semplici (es. conteggi per categoria).
export function bar(elementId, labels, values, yTitle, colors) {
    const e = el(elementId);
    if (!e) return;
    const data = [{
        type: 'bar',
        x: labels,
        y: values,
        marker: { color: colors || '#2962FF' },
        hovertemplate: `%{x}: %{y}<extra></extra>`,
    }];
    const layout = {
        margin: { l: 50, r: 20, t: 10, b: 60 },
        yaxis: { title: yTitle },
        xaxis: { automargin: true },
        height: 300,
        ...TRANSPARENT,
    };
    window.Plotly.react(e, data, layout, { displayModeBar: false, responsive: true });
}

// Serie temporali multiple. traces: [{ name, x:[unixSec], y:[num], color }].
export function timeseries(elementId, traces, yTitle) {
    const e = el(elementId);
    if (!e) return;
    const data = traces.map(t => ({
        type: 'scatter',
        mode: 'lines',
        name: t.name,
        x: t.x.map(s => new Date(s * 1000)),
        y: t.y,
        line: { color: t.color, width: 2 },
    }));
    const layout = {
        margin: { l: 55, r: 20, t: 10, b: 40 },
        yaxis: { title: yTitle },
        height: 300,
        legend: { orientation: 'h' },
        ...TRANSPARENT,
    };
    window.Plotly.react(e, data, layout, { displayModeBar: false, responsive: true });
}

// Ciambella (es. ripartizione di un contatore per etichetta).
export function donut(elementId, labels, values) {
    const e = el(elementId);
    if (!e) return;
    const data = [{
        type: 'pie',
        hole: 0.55,
        labels: labels,
        values: values,
        textinfo: 'label+value',
    }];
    const layout = { margin: { l: 10, r: 10, t: 10, b: 10 }, height: 280, showlegend: false, ...TRANSPARENT };
    window.Plotly.react(e, data, layout, { displayModeBar: false, responsive: true });
}

export function dispose(elementId) {
    const e = document.getElementById(elementId);
    if (e && window.Plotly) window.Plotly.purge(e);
}
