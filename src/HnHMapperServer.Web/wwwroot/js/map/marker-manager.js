// Marker Manager Module
// Handles game marker (resource, quest, etc.) management

import { HnHMaxZoom } from './leaflet-config.js';

// Marker storage
const markers = {};
let currentMapId = 0;
let markerLayer = null;
let detailedMarkerLayer = null;

// Safely invoke .NET methods from JS
let invokeDotNetSafe = null;

/**
 * Initialize marker manager
 * @param {function} invokeFunc - Function to invoke .NET methods
 */
export function initializeMarkerManager(invokeFunc) {
    invokeDotNetSafe = invokeFunc;
}

/**
 * Set marker layers
 * @param {object} mainLayer - Main marker layer
 * @param {object} detailLayer - Detailed marker layer (for high zoom)
 */
export function setMarkerLayers(mainLayer, detailLayer) {
    markerLayer = mainLayer;
    detailedMarkerLayer = detailLayer;
}

/**
 * Set the current map ID for marker filtering
 */
export function setCurrentMapId(mapId) {
    currentMapId = mapId;
}

/**
 * Add a marker to the map
 * @param {object} markerData - Marker data with id, name, image, position, type, ready, minReady, maxReady, hidden
 * @param {object} mapInstance - Leaflet map instance
 * @returns {boolean} - True if marker was added
 */
export function addMarker(markerData, mapInstance) {
    // Only show markers on their own map
    if (markerData.map !== currentMapId) {
        return false;
    }

    if (markers[markerData.id]) {
        return false;
    }

    if (markerData.hidden) {
        return false;
    }

    const iconUrl = `${markerData.image}.png`;
    const isCustom = markerData.image === "gfx/terobjs/mm/custom";
    const isCave = markerData.name.toLowerCase() === "cave";

    let iconSize = isCustom && !isCave ? [36, 36] : [36, 36];
    let iconAnchor = isCustom && !isCave ? [11, 21] : [18, 18];

    let icon;
    if (markerData.timerText) {
        // Use divIcon for markers with timers to allow HTML overlay
        const actualIconUrl = isCave ? 'gfx/hud/mmap/cave.png' : iconUrl;
        icon = L.divIcon({
            html: `
                <div class="marker-with-timer">
                    <img src="${actualIconUrl}" class="marker-icon-img" style="width: ${iconSize[0]}px; height: ${iconSize[1]}px;" />
                    <div class="marker-timer-text">${markerData.timerText}</div>
                </div>
            `,
            iconSize: [iconSize[0], iconSize[1] + 12], // Add 12px for timer text
            iconAnchor: [iconAnchor[0], iconAnchor[1] + 12],
            popupAnchor: [0, -(iconAnchor[1] + 12)],
            className: 'marker-timer-container'
        });
    } else {
        // Use regular icon for markers without timers
        icon = L.icon({
            iconUrl: isCave ? 'gfx/hud/mmap/cave.png' : iconUrl,
            iconSize: iconSize,
            iconAnchor: iconAnchor
        });
    }

    const position = mapInstance.unproject([markerData.position.x, markerData.position.y], HnHMaxZoom);
    const marker = L.marker(position, { icon: icon, riseOnHover: true });

    const color = getMarkerColor(markerData.type);
    const extra = getMarkerReadyText(markerData);

    marker.bindTooltip(`<div style='color:${color};'><b>${markerData.name} ${extra}</b></div>`, {
        permanent: false,
        direction: 'top',
        sticky: true,
        opacity: 0.9
    });

    marker.on('click', () => {
        invokeDotNetSafe('JsOnMarkerClicked', markerData.id);
    });

    marker.on('contextmenu', (e) => {
        L.DomEvent.stopPropagation(e);
        // Pass screen coordinates for proper context menu positioning
        const screenX = Math.floor(e.containerPoint.x);
        const screenY = Math.floor(e.containerPoint.y);
        invokeDotNetSafe('JsOnMarkerContextMenu', markerData.id, screenX, screenY);
    });

    // Add to appropriate layer
    if (markerData.image === "gfx/hud/mmap/cave" || markerData.image === "gfx/terobjs/mm/burrow") {
        marker.addTo(detailedMarkerLayer);
    } else {
        marker.addTo(markerLayer);
    }

    markers[markerData.id] = {
        marker: marker,
        data: markerData
    };

    return true;
}

/**
 * Add multiple markers to the map in a single batch (performance optimization)
 * @param {Array} markersData - Array of marker data objects
 * @param {object} mapInstance - Leaflet map instance
 * @returns {object} - Result with counts: { added, skipped }
 */
export function addMarkersBatch(markersData, mapInstance) {
    let added = 0;
    let skipped = 0;

    for (const markerData of markersData) {
        if (addMarker(markerData, mapInstance)) {
            added++;
        } else {
            skipped++;
        }
    }

    return { added, skipped };
}

/**
 * Update an existing marker
 * @param {number} markerId - Marker ID
 * @param {object} markerData - Updated marker data
 * @param {object} mapInstance - Leaflet map instance
 * @returns {boolean} - True if marker was updated
 */
export function updateMarker(markerId, markerData, mapInstance) {
    const mark = markers[markerId];
    if (mark) {
        // Remove marker if it's no longer on the current map
        if (markerData.map !== currentMapId) {
            removeMarker(markerId, mapInstance);
            return false;
        }

        // Check if timer state has changed (added, removed, or text changed)
        const oldTimerText = mark.data.timerText || null;
        const newTimerText = markerData.timerText || null;

        if (oldTimerText !== newTimerText) {
            // Timer state changed - need to recreate marker with new icon
            // Remove old marker and add new one with updated timer border/text
            removeMarker(markerId, mapInstance);
            addMarker(markerData, mapInstance);
            return true;
        }

        // Update tooltip
        const extra = getMarkerReadyText(markerData);
        const color = getMarkerColor(markerData.type);
        mark.marker.setTooltipContent(`<div style='color:${color};'><b>${markerData.name} ${extra}</b></div>`);

        // Update position (in case coordinates changed)
        const position = mapInstance.unproject([markerData.position.x, markerData.position.y], HnHMaxZoom);
        mark.marker.setLatLng(position);

        mark.data = markerData;
        return true;
    }
    return false;
}

/**
 * Remove a marker from the map
 * @param {number} markerId - Marker ID to remove
 * @param {object} mapInstance - Leaflet map instance
 * @returns {boolean} - True if marker was removed
 */
export function removeMarker(markerId, mapInstance) {
    const mark = markers[markerId];
    if (mark) {
        mapInstance.removeLayer(mark.marker);
        delete markers[markerId];
        return true;
    }
    return false;
}

/**
 * Clear all markers from the map
 * @param {object} mapInstance - Leaflet map instance
 * @returns {boolean} - Always true
 */
export function clearAllMarkers(mapInstance) {
    Object.keys(markers).forEach(id => removeMarker(parseInt(id), mapInstance));
    return true;
}

/**
 * Toggle marker tooltips by type
 * @param {string} type - Marker type (quest, thingwall, etc.)
 * @param {boolean} visible - Whether to show tooltips
 * @returns {boolean} - Always true
 */
export function toggleMarkerTooltips(type, visible) {
    Object.values(markers).forEach(mark => {
        if (mark.data.type === type) {
            if (visible) {
                mark.marker.openTooltip();
            } else {
                mark.marker.closeTooltip();
            }
        }
    });
    return true;
}

/**
 * Jump map view to a marker
 * @param {number} markerId - Marker ID to jump to
 * @param {object} mapInstance - Leaflet map instance
 * @returns {boolean} - True if marker was found
 */
export function jumpToMarker(markerId, mapInstance) {
    const mark = markers[markerId];
    if (mark) {
        const latlng = mark.marker.getLatLng();
        mapInstance.setView(latlng, Math.max(mapInstance.getZoom(), 5));
        return true;
    }
    return false;
}

// Helper functions

function getMarkerColor(type) {
    if (type === "quest") return "#FDB800";
    if (type === "thingwall") return "#00cffd";
    return "#FFF";
}

function getMarkerReadyText(markerData) {
    if (markerData.ready) {
        return "READY";
    }
    if (markerData.maxReady !== -1 && markerData.minReady !== -1) {
        const now = Date.now();
        const minTime = msToTimeStr(markerData.minReady - now);
        const maxTime = msToTimeStr(markerData.maxReady - now);
        return `(Ready in [${minTime}] to [${maxTime}])`;
    }
    return "";
}

function msToTimeStr(duration) {
    if (duration < 0) duration = 0;

    const seconds = Math.floor((duration / 1000) % 60);
    const minutes = Math.floor((duration / (1000 * 60)) % 60);
    const hours = Math.floor((duration / (1000 * 60 * 60)) % 24);
    const days = Math.floor(duration / (1000 * 60 * 60 * 24));

    const d = days > 0 ? `${days}d` : "";
    const h = (days > 0 || hours > 0) ? `${hours}h ` : "";
    const m = (days <= 0 && (hours > 0 || minutes > 0)) ? `${minutes}m ` : "";
    const s = (days <= 0 && hours <= 0) ? `${seconds}s` : "";

    return d + h + m + s;
}
