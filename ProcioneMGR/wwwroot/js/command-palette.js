// Hotkey globale Ctrl+K (Cmd+K su Mac) per la ricerca globale.
// Il componente Blazor CommandPalette registra qui il proprio riferimento .NET;
// il listener è unico e sopravvive ai re-render del componente.
window.commandPalette = {
    _dotnetRef: null,
    _bound: false,

    init(dotnetRef) {
        this._dotnetRef = dotnetRef;
        if (this._bound) return;
        this._bound = true;
        document.addEventListener('keydown', (e) => {
            if ((e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K')) {
                e.preventDefault();
                this._dotnetRef?.invokeMethodAsync('OpenFromHotkey');
            }
        });
    },

    dispose() {
        this._dotnetRef = null;
    },

    focus(element) {
        element?.focus();
    }
};
