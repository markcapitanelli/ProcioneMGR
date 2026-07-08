// Heatmap 2D via Plotly per la mappa di robustezza dei parametri (Sharpe out-of-sample).

export function renderHeatmap(elementId, xValues, yValues, zValues, xLabel, yLabel) {
    const el = document.getElementById(elementId);
    if (!el || !window.Plotly) {
        return;
    }

    const data = [{
        type: 'heatmap',
        x: xValues,
        y: yValues,
        z: zValues,
        colorscale: [
            [0.0, '#d73027'], // basso = rosso
            [0.5, '#fee08b'],
            [1.0, '#1a9850'], // alto = verde
        ],
        colorbar: { title: 'Sharpe OOS' },
        hovertemplate: `${xLabel}: %{x}<br>${yLabel}: %{y}<br>Sharpe: %{z:.2f}<extra></extra>`,
    }];

    const layout = {
        margin: { t: 10, r: 10, b: 50, l: 60 },
        xaxis: { title: xLabel, type: 'category' },
        yaxis: { title: yLabel, type: 'category' },
        height: 420,
        paper_bgcolor: 'transparent',
        plot_bgcolor: 'transparent',
    };

    window.Plotly.react(el, data, layout, { displayModeBar: false, responsive: true });
}

export function dispose(elementId) {
    const el = document.getElementById(elementId);
    if (el && window.Plotly) {
        window.Plotly.purge(el);
    }
}
