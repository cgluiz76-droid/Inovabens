// Mapa de imóveis (menu Imóveis → Mapa): Leaflet + Leaflet.markercluster, sem chave de API.
// Satélite (Esri World Imagery) com nomes de lugares por padrão; camada "Ruas" (OpenStreetMap) no seletor.
// Pinos com o preço, agrupados quando próximos; imóveis no mesmo ponto se abrem em leque no zoom máximo.
// O Blazor manda os itens já filtrados; o mapa avisa o Blazor (AreaMudou) quais ficam visíveis na área.

const CDN = "https://cdnjs.cloudflare.com/ajax/libs/";
const CSS = [CDN + "leaflet/1.9.4/leaflet.min.css", CDN + "leaflet.markercluster/1.5.3/MarkerCluster.min.css",
    CDN + "leaflet.markercluster/1.5.3/MarkerCluster.Default.min.css"];
const JS = [CDN + "leaflet/1.9.4/leaflet.min.js", CDN + "leaflet.markercluster/1.5.3/leaflet.markercluster.min.js"];

let carregando;
function carregarBibliotecas() {
    if (window.L?.markerClusterGroup) return Promise.resolve();
    carregando ??= (async () => {
        for (const href of CSS) {
            if (document.querySelector(`link[href="${href}"]`)) continue;
            const l = document.createElement("link");
            l.rel = "stylesheet"; l.href = href;
            document.head.appendChild(l);
        }
        for (const src of JS) {
            await new Promise((ok, erro) => {
                const s = document.createElement("script");
                s.src = src; s.onload = ok; s.onerror = () => erro(new Error("Não foi possível carregar o mapa (" + src + ")."));
                document.head.appendChild(s);
            });
        }
    })();
    return carregando;
}

const esc = (t) => String(t ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

function precoCurto(v) {
    if (v == null) return "Consulte";
    if (v >= 1e6) return "R$ " + (v / 1e6).toLocaleString("pt-BR", { maximumFractionDigits: 2 }) + " mi";
    if (v >= 1e3) return "R$ " + Math.round(v / 1e3).toLocaleString("pt-BR") + " mil";
    return "R$ " + Math.round(v).toLocaleString("pt-BR");
}

const mapas = new Map();

export async function iniciar(elemento, dotnet) {
    await carregarBibliotecas();
    const L = window.L;
    const satelite = L.layerGroup([
        L.tileLayer("https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}",
            { maxZoom: 19, attribution: "Imagens © Esri, Maxar, Earthstar Geographics" }),
        L.tileLayer("https://server.arcgisonline.com/ArcGIS/rest/services/Reference/World_Boundaries_and_Places/MapServer/tile/{z}/{y}/{x}",
            { maxZoom: 19 }),
    ]);
    const ruas = L.tileLayer("https://tile.openstreetmap.org/{z}/{x}/{y}.png", { maxZoom: 19, attribution: "© OpenStreetMap" });
    const mapa = L.map(elemento, { layers: [satelite] }).setView([-20.8, -49.4], 8);
    L.control.layers({ "Satélite": satelite, "Ruas": ruas }, null, { position: "topright" }).addTo(mapa);

    const grupo = L.markerClusterGroup({ showCoverageOnHover: false, spiderfyOnMaxZoom: true, maxClusterRadius: 50, chunkedLoading: true });
    mapa.addLayer(grupo);

    // o contêiner muda de tamanho (carga das bibliotecas, janela, celular): sem isso sobra faixa cinza sem imagem
    const observador = new ResizeObserver(() => mapa.invalidateSize());
    observador.observe(elemento);
    const estado = { mapa, grupo, dotnet, observador, marcas: new Map(), itens: [] };
    mapa.on("moveend", () => avisarArea(estado));
    mapas.set(elemento, estado);
}

function avisarArea(estado) {
    const b = estado.mapa.getBounds();
    const ids = estado.itens.filter((i) => b.contains([i.lat, i.lng])).map((i) => i.id);
    estado.dotnet.invokeMethodAsync("AreaMudou", ids).catch(() => { });
}

export function desenhar(elemento, itens, ajustar) {
    const estado = mapas.get(elemento);
    if (!estado) return;
    const L = window.L;
    estado.grupo.clearLayers();
    estado.marcas.clear();
    estado.itens = itens;
    const marcas = itens.map((i) => {
        const classe = "pino-imovel " + (i.cor ?? "");
        const m = L.marker([i.lat, i.lng], {
            title: i.codigo + " · " + i.titulo,
            icon: L.divIcon({ className: "", html: `<div class="${esc(classe)}">${esc(precoCurto(i.preco))}</div>`, iconSize: null }),
        });
        const capa = i.capa ? `<img src="${esc(i.capa)}" alt="" loading="lazy">` : "";
        m.bindPopup(`<div class="balao-imovel">${capa}<b>${esc(i.codigo)}</b><span>${esc(i.titulo)}</span>`
            + `<span class="mudo">${esc(i.local)}</span><strong>${esc(i.precoCheio)}</strong><span class="mudo">${esc(i.situacao)}</span>`
            + `<div class="links"><a href="imoveis/${esc(i.id)}">Abrir ficha</a>`
            + `<a href="${esc(i.maps)}" target="_blank" rel="noopener">Google Maps ↗</a></div></div>`, { minWidth: 220, maxWidth: 260 });
        estado.marcas.set(i.id, m);
        return m;
    });
    estado.grupo.addLayers(marcas);
    estado.mapa.invalidateSize();
    if (ajustar && itens.length)
        estado.mapa.fitBounds(L.latLngBounds(itens.map((i) => [i.lat, i.lng])).pad(0.15), { maxZoom: 16 });
    avisarArea(estado);
}

export function destacar(elemento, id) {
    const estado = mapas.get(elemento);
    if (!estado) return;
    elemento.querySelectorAll(".pino-imovel.ativo").forEach((p) => p.classList.remove("ativo"));
    const el = id && estado.marcas.get(id)?.getElement();
    el?.querySelector(".pino-imovel")?.classList.add("ativo");
}

export function focar(elemento, id) {
    const estado = mapas.get(elemento);
    const m = estado?.marcas.get(id);
    if (m) estado.grupo.zoomToShowLayer(m, () => m.openPopup());
}

export function destruir(elemento) {
    const estado = mapas.get(elemento);
    if (!estado) return;
    estado.observador.disconnect();
    estado.mapa.remove();
    mapas.delete(elemento);
}
