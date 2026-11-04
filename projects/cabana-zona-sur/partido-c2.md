# Cabaña Zona Sur — partido C2 "dos piezas en diagonal" (decidido 2026-11-04 por el owner)

Decisiones del owner:
- Partido C (dos piezas desplazadas) con bisagra diagonal NO–SE que pasa por la salamandra. Dirección de bisagra: OK.
- La salamandra DEBE quedar centrada; circulación en anillo alrededor.
- Llenos y vacíos: vacío NO = semicubierto (el más generoso); vacío SE = ingreso + antecámara (doble puerta).
- Gestos rectos diagonales (no ortogonales), formas que descarguen la nieve.
- Cada pieza: un agua empinado (35–40°) que cae hacia afuera; cumbrera en la bisagra sobre el fuego; chimenea en el punto alto; sin limahoyas.
  Madera cae al NE (como el corte); el sol norte entra por el frente vidriado diagonal norte.
- Cubiertas: chapa exterior, madera (machimbre) interior. Muros de la pieza de piedra: piedra vista (piedra es piedra).
- Pieza de piedra (SO): baño/núcleo húmedo que abraza la roca del sitio, baja y pesada, contra viento SO.
- Pieza de madera (NE): estar, más alta, vidriada al norte.

Coordenadas provisorias (mm, proyecto; salamandra existente en 64000,15000 = centro):
- Madera: (62400,16600) (63200,18600) (69600,19200) (69200,14600) (65600,13400); vidriado en lado (63200,18600)-(69600,19200).
- Piedra: (62400,16600) (65600,13400) (64800,11000) (58400,10600) (58800,15400).
- Semicubierto NO: (58800,15400) (62400,16600) (63200,18600) (57200,19600).
- Vacío SE ingreso: (65600,13400) (69200,14600) (69200,11400) (64800,11000); antecámara ~2.4x2.4 m dentro, acceso desde sendero al S.

- Owner (2026-11-04): modelar C2 AL LADO de la cabaña actual (no reemplazar); comparar y borrar después la que no quede. Desplazar todas las coordenadas (p.ej. +20000 en X) evitando árboles/rocas del sitio.

## Modelado 2026-11-04 (real Revit, pipe 3888) — guardado con document.save
Fuego F=(85500,13500); d=(0.707,-0.707) bisagra hacia SE; m=(0.707,0.707) hacia NE.
- Madera: WA(82106,16894) WB(86631,12369) WC(90379,14985) WD(84722,20642); techo 1 agua cae a NE, base 3000, 35°, alto 6151 en bisagra.
- Piedra: SA(84369,14631) SB(88894,10106) SC(85712,6924) SD(81187,11449); techo cae a SO, base 2400, 35°, alto 5551.
- Vidrio: WA-WD y WD-WC (h3000), SA-WA (h3000 + madera arriba); lucernario SA-WB base 5551 h600.
- Muros piedra provisorios "Exterior - Brick on CMU" 490; madera "Generic - 200mm". Techos "Cabaña - chapa, aislación y machimbre".
- Salamandra DOMOFOCUS b171c7aa-...-00034968 en F. Vista "C2 3D".
Pendiente: materiales reales (piedra/lenga/chapa negra), puertas antecámara, semicubierto (techo), piso, tabiques baño.

## 2026-11-04 tarde — C2 RECHAZADA por el owner ("no encuentro lógica"); pidió algo como diapositivas 13-14 (Garibaldi). Owner borró C2 a mano.
## C3 "volumen tallado" (Garibaldi) modelada y guardada
- Rectángulo 6x9 m x 82500..88500, y 9000..18000; cumbrera N-S; techo 50°, alero z2600, cumbrera z6175; zócalo hormigón (Foundation 300) -500..+400; muros desde +400.
- Esquina NE tallada (x 86100..88500, y 16000..18000) = semicubierto/antecámara, muros "C3 Porche madera" (lenga).
- Envolvente "C3 Envolvente chapa negra" (chapa negra ext / lenga int); hastial sur usa "(cara invertida)" porque Revit pone la cara exterior a la IZQUIERDA del sentido de dibujo. Techo "C3 Cubierta chapa negra y lenga".
- Salamandra DOMOFOCUS en (85500,13500) centro (z 0; falta subir a +400).
- Pendiente: vidriado norte, puerta(s) antecámara, núcleo de piedra al sur (baño+roca), piso, ventanas laterales.
- Lecciones: elevación "East" del proyecto corta DENTRO de C3 (marcador x≈87750); vista 3D por defecto mira desde el NO.

## 2026-11-04 — C3 descartada por "casita de nene"; owner pidió análisis del PDF + discurso + prompt ANTES de modelar.
Doc Claude Docs "Cabaña Zona Sur — Análisis de referencias, discurso y prompt de modelado" (https://claude.ai/artifact/FyURVSor2xB23mHjB6eLj7) = fuente de verdad del diseño C4.
## C4 "Piedra y piel" modelada y guardada (19:11 UTC)
- Eje SO→NE alineado al viento; trapecio negro 9 m, 5→7 m; techo 50° faldones SE/NO + limatesa SO; proa NE en voladizo 1,5 m; raja vidriada 2 m.
- Bloque de piedra (C4 Muro piedra 500) al SO con boca excavada en embudo en cara SE, puerta en muro de lenga; cubierta pesada plana +2700; muro térmico de piedra.
- Salamandra a556 a57edd83-...-00034a91 en (86500,14700) +400. Techo piel a57edd83-...-00034a65; techo piedra ...-00034a7c.
- Pendiente: puertas (exterior/interior), ventanas laterales angostas, piso, interior lenga, roca, vista 3D desde SE/SO (falta view_from).
