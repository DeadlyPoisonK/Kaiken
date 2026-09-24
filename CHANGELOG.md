# Cambios

## 1.12.0 (2026-09-24)

- **Fix:** Kaiken ya no deja de cargar cuando hay dos Revit abiertos a la vez (ej. 2025 y 2026). Antes, si el puerto del puente en vivo estaba ocupado, Revit descartaba todo el add-in con "Revit cannot run the external application".
- **Fix:** se compila contra la versión base de la API de cada año (2025.0 / 2026.0), así funciona en cualquier actualización de Revit y desaparece el aviso "Assembly version conflict".
- Nueva configuración en `%APPDATA%\Kaiken\settings.json`: nombres de los parámetros de revisión y fecha, formato del Número de Plano, y carpeta, códigos y vista del IFC Batch dejan de estar fijos en el código.
- El puente en vivo (servidor local para agentes de IA) queda apagado por defecto.
- El botón **Avoider** pasa a llamarse **Pontifex** (por el puente de Da Vinci: el salto en arco sobre el obstáculo).
- **Fix Pontifex con varios cruces:**
  - Entre dos saltos seguidos se deja recta para los DOS codos (antes solo para uno), y los saltos demasiado juntos se fusionan considerando esa holgura. Antes los codos no cabían o el ángulo quedaba forzado.
  - Al unir obstáculos cercanos, el desvío se calcula con la caja de TODOS ellos (antes solo con el primero, y el salto podía chocar con el segundo).
  - Escalerillas y conduits: al cortar un tramo que ya tenía un codo de un salto anterior, el codo queda conectado al tramo correcto (antes Revit lo arrastraba y el salto anterior quedaba roto).
  - Cada salto va en su propia sub-transacción: si uno falla, se deshace completo y no deja el elemento cortado ni arruina los demás.
  - Un salto que no cabe hasta el salto anterior se omite con aviso en vez de intentar cortar fuera del tramo.
- Pontifex: detecta cruces donde el obstáculo toca el cuerpo del elemento pero no su eje (p.ej. tuberías que se cruzan a distinta altura).
- Pontifex: arreglado el cálculo de posición de cruces con elementos de modelos vinculados (se mezclaban coordenadas del link y del host), y es mucho más rápido en links grandes.
- Exportar IFC Batch: sin configuración, exporta todos los modelos abiertos usando la vista `{3D}` y pregunta la carpeta de destino.

## 1.10.0

- Botón **Unir** (panel MEP): une dos tramos paralelos a distinta altura con un salto de dos codos (45°+45° o 90°+90°).
- Pontifex: los errores se muestran con un mensaje propio en vez del genérico de Revit.
- Instalador combinado para Revit 2025 y 2026.

## 1.9.0

- Rou-T, Rou-C y Ceiling-Alt funcionan con tuberías, ductos, escalerillas y conduits.

## 1.8.0

- Botón **Ceiling-Alt**: salto de nivel con dos codos de 90° al cruzar un muro.

## 1.4.0 – 1.5.0

- **Rou-T**: corrige en un click una Te de ramal que cruza otras tuberías, con opción de conectar hacia abajo.

## 1.2.0 – 1.3.0

- Paneles **Enchufes** (escanear DXF y colocar familias) y **MEP** (Pontifex).

## 1.1.0

- Exportar DWG, PDF y DWF.
