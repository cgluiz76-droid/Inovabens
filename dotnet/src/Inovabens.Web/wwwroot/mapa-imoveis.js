// Mapa de imóveis em tela cheia (menu Imóveis → Mapa).
// MapLibre GL JS (WebGL: zoom e arraste suaves, sem chave de API) + Supercluster para agrupar alfinetes próximos.
// Base "Satélite" = Esri World Imagery com nomes de lugares; base "Ruas" = OpenFreeMap (vetorial, gratuito).
// Cada imóvel é um alfinete HTML com a referência e o preço; passar o mouse mostra o resumo, clicar abre a página
// do imóvel no site. Os alfinetes são HTML (não camadas do estilo), por isso continuam ao trocar a base.

const CDN = "https://cdnjs.cloudflare.com/ajax/libs/maplibre-gl/5.15.0/";
const SUPERCLUSTER = "https://cdn.jsdelivr.net/npm/supercluster@8.0.1/dist/supercluster.min.js";

/** Sem filtro: São José do Rio Preto, raio de ~20 km (o usuário amplia depois). */
const RIO_PRETO = { lat: -20.8197, lng: -49.3794, raioKm: 20 };

const ESTILO_SATELITE = {
    version: 8,
    sources: {
        satelite: {
            type: "raster", tileSize: 256, maxzoom: 19,
            tiles: ["https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}"],
            attribution: "Imagens © Esri, Maxar, Earthstar Geographics",
        },
        rotulos: {
            type: "raster", tileSize: 256, maxzoom: 19,
            tiles: ["https://server.arcgisonline.com/ArcGIS/rest/services/Reference/World_Boundaries_and_Places/MapServer/tile/{z}/{y}/{x}"],
        },
    },
    layers: [
        { id: "satelite", type: "raster", source: "satelite" },
        { id: "rotulos", type: "raster", source: "rotulos" },
    ],
};
const ESTILO_RUAS = "https://tiles.openfreemap.org/styles/liberty";

let carregando;
function carregarBibliotecas() {
    if (window.maplibregl && window.Supercluster) return Promise.resolve();
    carregando ??= (async () => {
        if (!document.querySelector(`link[href="${CDN}maplibre-gl.min.css"]`)) {
            const l = document.createElement("link");
            l.rel = "stylesheet"; l.href = CDN + "maplibre-gl.min.css";
            document.head.appendChild(l);
        }
        for (const src of [CDN + "maplibre-gl.min.js", SUPERCLUSTER]) {
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

/** Retângulo que contém o círculo de raio r km em volta do ponto. */
function caixaDoRaio({ lat, lng, raioKm }) {
    const dLat = raioKm / 111.32;
    const dLng = raioKm / (111.32 * Math.cos(lat * Math.PI / 180));
    return [[lng - dLng, lat - dLat], [lng + dLng, lat + dLat]];
}

const mapas = new Map();

export async function iniciar(elemento, dotnet) {
    await carregarBibliotecas();
    const ml = window.maplibregl;
    const mapa = new ml.Map({
        container: elemento,
        style: ESTILO_SATELITE,
        bounds: caixaDoRaio(RIO_PRETO),
        maxZoom: 19,
        attributionControl: { compact: true },
        cooperativeGestures: false,
    });
    mapa.addControl(new ml.NavigationControl({ visualizePitch: true }), "bottom-right");
    mapa.addControl(new ml.ScaleControl({ unit: "metric" }), "bottom-left");

    const estado = {
        mapa, dotnet, itens: [], porId: new Map(), indice: null, marcas: new Map(),
        resumo: new ml.Popup({ closeButton: false, closeOnClick: false, offset: [0, -48], maxWidth: "280px", className: "resumo-imovel" }),
        lista: new ml.Popup({ closeButton: true, closeOnClick: true, offset: 18, maxWidth: "300px", className: "lista-imoveis" }),
    };
    mapa.on("moveend", () => atualizar(estado));
    estado.tecla = (e) => { if (e.key === "Escape") dotnet.invokeMethodAsync("Fechar").catch(() => { }); };
    document.addEventListener("keydown", estado.tecla);
    estado.observador = new ResizeObserver(() => mapa.resize());
    estado.observador.observe(elemento);
    mapas.set(elemento, estado);
}

/** Troca a base (Satélite / Ruas). Os alfinetes são HTML e continuam no lugar. */
export function base(elemento, nome) {
    const estado = mapas.get(elemento);
    estado?.mapa.setStyle(nome === "ruas" ? ESTILO_RUAS : ESTILO_SATELITE);
}

/**
 * Recebe os imóveis com coordenadas. focoPadrao = sem filtro ativo: abre em Rio Preto (raio 20 km);
 * com filtro, enquadra todos os resultados.
 */
export function desenhar(elemento, itens, focoPadrao) {
    const estado = mapas.get(elemento);
    if (!estado) return;
    estado.itens = itens;
    estado.porId = new Map(itens.map((i) => [i.id, i]));
    estado.indice = new window.Supercluster({ radius: 56, maxZoom: 20 }).load(itens.map((i) => ({
        type: "Feature", geometry: { type: "Point", coordinates: [i.lng, i.lat] }, properties: { id: i.id },
    })));
    for (const m of estado.marcas.values()) m.remove();
    estado.marcas.clear();
    estado.resumo.remove();
    estado.lista.remove();

    if (focoPadrao || itens.length === 0) {
        estado.mapa.fitBounds(caixaDoRaio(RIO_PRETO), { duration: 0 });
    } else if (itens.length === 1) {
        estado.mapa.jumpTo({ center: [itens[0].lng, itens[0].lat], zoom: 16 });
    } else {
        const lngs = itens.map((i) => i.lng), lats = itens.map((i) => i.lat);
        estado.mapa.fitBounds([[Math.min(...lngs), Math.min(...lats)], [Math.max(...lngs), Math.max(...lats)]],
            { padding: { top: 90, bottom: 60, left: 60, right: 60 }, maxZoom: 16, duration: 0 });
    }
    atualizar(estado);
}

/** Recalcula grupos e alfinetes para o trecho visível (só cria o que mudou). */
function atualizar(estado) {
    if (!estado.indice) return;
    const ml = window.maplibregl;
    const b = estado.mapa.getBounds();
    const zoom = Math.floor(estado.mapa.getZoom());
    const grupos = estado.indice.getClusters([b.getWest() - 0.05, b.getSouth() - 0.05, b.getEast() + 0.05, b.getNorth() + 0.05], zoom);
    const vistos = new Set();
    for (const g of grupos) {
        const p = g.properties;
        const chave = p.cluster ? "g" + p.cluster_id + "_" + zoom : "i" + p.id;
        vistos.add(chave);
        if (estado.marcas.has(chave)) continue;
        const [lng, lat] = g.geometry.coordinates;
        const el = p.cluster ? elementoGrupo(estado, g) : elementoAlfinete(estado, estado.porId.get(p.id));
        const marca = new ml.Marker({ element: el, anchor: p.cluster ? "center" : "bottom" }).setLngLat([lng, lat]).addTo(estado.mapa);
        estado.marcas.set(chave, marca);
    }
    for (const [chave, m] of estado.marcas) {
        if (!vistos.has(chave)) { m.remove(); estado.marcas.delete(chave); }
    }
    // avisa a tela quantos estão no trecho visível
    const ids = estado.itens.filter((i) => b.contains([i.lng, i.lat])).length;
    estado.dotnet.invokeMethodAsync("AreaMudou", ids).catch(() => { });
}

function elementoAlfinete(estado, i) {
    const el = document.createElement("button");
    el.type = "button";
    el.className = "alfinete " + (i.cor || "");
    el.setAttribute("aria-label", `${i.rotulo} — ${i.titulo} — abrir no site`);
    el.innerHTML = `<span class="alfinete-etiqueta"><b>${esc(i.rotulo)}</b><span>${esc(precoCurto(i.preco))}</span></span>`
        + `<svg class="alfinete-icone" viewBox="0 0 24 34" aria-hidden="true"><path d="M12 0C5.4 0 0 5.3 0 11.9 0 20.8 12 34 12 34s12-13.2 12-22.1C24 5.3 18.6 0 12 0z"/><circle cx="12" cy="12" r="4.6"/></svg>`;
    el.addEventListener("mouseenter", () => mostrarResumo(estado, i));
    el.addEventListener("mouseleave", () => estado.resumo.remove());
    el.addEventListener("click", (e) => { e.stopPropagation(); abrirSite(i); });
    return el;
}

function elementoGrupo(estado, g) {
    const n = g.properties.point_count;
    const el = document.createElement("button");
    el.type = "button";
    el.className = "grupo-alfinetes";
    const lado = n < 10 ? 38 : n < 50 ? 46 : 54;
    el.style.width = el.style.height = lado + "px";
    el.textContent = n;
    el.setAttribute("aria-label", n + " imóveis — aproximar");
    el.addEventListener("click", (e) => {
        e.stopPropagation();
        const id = g.properties.cluster_id;
        const zoom = estado.indice.getClusterExpansionZoom(id);
        if (zoom > estado.mapa.getMaxZoom()) {
            // mesmo ponto (ex.: lotes no mesmo endereço): lista para escolher
            const folhas = estado.indice.getLeaves(id, Infinity).map((f) => estado.porId.get(f.properties.id));
            estado.lista.setLngLat(g.geometry.coordinates).setHTML(
                `<div class="lista-titulo">${n} imóveis neste ponto</div>` + folhas.map((i) =>
                    `<a href="${esc(i.site)}" target="_blank" rel="noopener">${i.capa ? `<img src="${esc(i.capa)}" alt="">` : "<span class=\"sem-capa\"></span>"}`
                    + `<span><b>${esc(i.rotulo)}</b> · ${esc(precoCurto(i.preco))}<br><small>${esc(i.titulo)}</small></span></a>`).join(""),
            ).addTo(estado.mapa);
        } else {
            estado.mapa.easeTo({ center: g.geometry.coordinates, zoom });
        }
    });
    return el;
}

function mostrarResumo(estado, i) {
    const capa = i.capa ? `<img src="${esc(i.capa)}" alt="" loading="lazy">` : "";
    estado.resumo.setLngLat([i.lng, i.lat]).setHTML(
        `${capa}<div class="resumo-corpo"><span class="resumo-ref">${esc(i.rotulo)} · ${esc(i.situacao)}</span>`
        + `<b>${esc(i.titulo)}</b><span class="resumo-local">${esc(i.local)}</span><strong>${esc(i.precoCheio)}</strong>`
        + `<span class="resumo-dica">Clique para abrir no site</span></div>`).addTo(estado.mapa);
}

function abrirSite(i) {
    window.open(i.site, "_blank", "noopener");
}

export function destruir(elemento) {
    const estado = mapas.get(elemento);
    if (!estado) return;
    document.removeEventListener("keydown", estado.tecla);
    estado.observador.disconnect();
    estado.mapa.remove();
    mapas.delete(elemento);
}
