# O que olhar numa vitrine de veículos

Referência da skill `autohous-website-audit`. O que segue é o que separa uma vitrine
que vende de uma que existe.

## Encontrar a vitrine

Nem sempre está no menu principal, e nem sempre está no mesmo domínio. Procure, nesta
ordem:

- "Estoque", "Seminovos", "Usados", "Nossos veículos", "Comprar"
- Um subdomínio: `seminovos.`, `estoque.`, `usados.`
- Um link para plataforma de terceiro no rodapé

**Vitrine em subdomínio ou domínio separado é um achado por si só**: significa que o
institucional e o estoque são dois sistemas, mantidos por gente diferente, e quase
sempre com dados que divergem.

## Contagem de veículos

Só registre `approximate_count` com **contagem ou paginação visível**:

- "324 veículos encontrados" — use
- "Página 1 de 27", com 12 por página — use, ~324
- Uma grade que carrega infinitamente sem total — **não** use
- "Somos o maior estoque da região" — **não** use, é marketing

Estimar pelo tamanho da empresa é o erro mais caro possível aqui: o número sai desta
auditoria e entra numa frase dita a um dono de concessionária que sabe exatamente
quantos carros tem.

## Qualidade da vitrine

| O quê | Por que importa |
|---|---|
| Filtro por preço, marca, ano, km | Sem isso o visitante desiste na terceira rolagem |
| Página de detalhe por veículo | Sem URL própria, não há tráfego orgânico nem link compartilhável |
| Fotos reais, e não a foto de catálogo da montadora | Foto de catálogo em seminovo é sinal de vitrine automatizada sem curadoria |
| Preço visível | "Consulte" espanta e derruba conversão |
| Veículo vendido ainda listado | Sinal forte de estoque não sincronizado — a dor central |

## Sinais de fragmentação

O que procurar, porque alimenta `multiple_portals` e `complex_integration`:

- O mesmo veículo com **preços diferentes** no site e no portal
- Contagem do site divergindo da contagem no Webmotors
- Dois "fale conosco" que vão para lugares diferentes
- Rodapé citando um sistema, e a vitrine sendo servida por outro

## Conversão, no contexto brasileiro

- **WhatsApp não é enfeite.** É onde a negociação acontece de fato. Ausente, ou
  escondido só no rodapé, é `medium` no mínimo.
- **Simulador de financiamento** — a maioria das vendas é financiada; sem simulador o
  lead vai embora calcular em outro lugar.
- **Avaliação de usado (troca)** — grande parte das compras envolve um carro na troca.
- **Agendamento** — test-drive e revisão; mais comum em concessionária que em revenda.
