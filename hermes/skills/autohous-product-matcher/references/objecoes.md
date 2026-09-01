# Objeções comuns no varejo automotivo brasileiro

Material de apoio para o campo `objections[]`. Não é roteiro para copiar: a objeção
registrada precisa ser a que **esta** pessoa, nesta empresa, provavelmente levanta — e
a resposta não pode prometer o que não foi verificado.

## Por persona

| Persona | O que ela costuma responder | O que a resposta não pode fazer |
|---|---|---|
| Sócio / Diretor Geral | "Já gastei com site e não deu resultado" | Prometer resultado. O histórico ruim dele é real |
| Diretor Comercial | "Meu problema é vendedor, não sistema" | Discordar. O problema dele provavelmente é os dois |
| Diretor de Marketing | "Quem cuida disso é a agência" | Atacar a agência. Ela é quem aprova ou veta |
| CTO / Head de TI | "Integrar com nosso DMS é inviável" | Afirmar que integra. Isso não foi verificado |
| Gerente de Loja | "Isso é decisão da matriz" | Insistir. É pedido de encaminhamento, não recusa |

## Por produto

**FrontCar** — "acabamos de refazer o site". Vale checar a data do que foi observado
antes de escrever o ângulo: um site refeito há dois meses com os problemas que a
auditoria achou é uma conversa; um refeito na semana passada é outra.

**MotorHub** — "os portais já sincronizam sozinhos". Às vezes é verdade para um dos
portais e não para os outros. A evidência precisa dizer qual.

**AutoFollow** — "já temos CRM". Ter CRM e usar o CRM são coisas diferentes, e a
plataforma sabe distinguir: se a auditoria detectou CRM, o critério
`captura_sem_destino` pontuou baixo e o produto provavelmente não é a porta de entrada.

**AutoTalk** — "temos WhatsApp". Quase sempre têm. O critério que sustenta este produto
é atrito de contato medido, não ausência de canal.

**BoxTech** — "somos pequenos demais para isso". Se o diagnóstico pontuou porte alto, a
evidência de porte é o que responde — número de unidades, CNPJs, marcas.

## O que nunca entra numa resposta

- Número que não está em `evidence[]`.
- Comparação com concorrente nomeado.
- Promessa de prazo, preço ou resultado.
- Afirmação sobre sistema interno que a auditoria não observou.
