# Dado pessoal — o que entra e o que não entra

O frame 09 dos boards define a política. A plataforma impõe parte dela por código
(`ContactPolicy`, `EvidenceFirstGuard`); o resto depende de você.

## O princípio

Você registra a pessoa **no papel dela na empresa**, não a pessoa.

A base legal de uma abordagem B2B é o interesse legítimo sobre um contato profissional
publicado. Ela não cobre dado de vida privada, e não cobre alguém que não exerce papel
de decisão ali.

## Entra

| Dado | Condição |
|---|---|
| Nome completo | como publicado, sem completar nem corrigir |
| Cargo e departamento | como escrito na fonte |
| E-mail corporativo | publicado, com fonte própria |
| Telefone da empresa | publicado como canal de contato |
| Perfil profissional público | LinkedIn, com vínculo atual verificado |
| E-mail em provedor pessoal | **só** quando publicado como contato da empresa |

A última linha existe porque revenda pequena opera assim de verdade. A plataforma o
marca como pessoal, e ele não conta como e-mail profissional na pontuação — mas
descartá-lo deixaria a conta sem contato nenhum.

## Não entra

- Endereço residencial, CPF, RG, data de nascimento.
- Estado civil, composição familiar, filiação.
- Telefone pessoal não publicado como canal profissional.
- Rede social pessoal — Instagram, Facebook, X — mesmo pública.
- Foto.
- Qualquer dado de pessoa que não exerce papel de decisão na empresa.
- Qualquer dado obtido de vazamento, base comprada ou agregador de dados pessoais.

A última é a mais importante e a mais fácil de violar sem perceber: um agregador que
devolve "e-mail e telefone de qualquer CPF" é exatamente a fonte que não pode aparecer
em `evidence[]`.

## Sócios na base da Receita

A plataforma já guarda `company_partners` a partir dos Dados Abertos da RFB, atrás de
opt-in (ADR-0008). Isso é dado público de registro empresarial, e é **insumo** — não é
contato.

Um sócio da base da RFB não vira `contacts` automaticamente: sócio não é
necessariamente decisor operacional, e o nome na Receita frequentemente é de alguém que
não trabalha no dia a dia. Se você identificar que o sócio **é** o decisor, registre
com a evidência que mostra isso — o site, a notícia, o perfil — e não com a base da
Receita como fonte.

## Confiança e o que ela significa aqui

- **0.9+** — a própria empresa publica a pessoa naquele cargo, com data recente.
- **0.7–0.9** — perfil profissional atual, ou notícia do setor dos últimos 12 meses.
- **0.5–0.7** — fonte única, indireta ou sem data clara.
- **abaixo de 0.5** — omita. A plataforma rejeita o run inteiro.

O piso não é burocracia. Um cargo errado manda a conversa para a pessoa errada dentro
da empresa certa, e queima a conta inteira — o interlocutor real fica sabendo que
alguém tentou por fora.

## Direito de exclusão

Um pedido de exclusão chega pela suppression list e é decisão humana (Regra 2). Você
nunca suprime, nem exclui, nem marca contato como inválido — você só descobre.
