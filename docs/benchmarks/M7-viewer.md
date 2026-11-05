# M7: replay and analysis viewer, manual checks 9–12 (spec 16.1)

Every check ran on the Release build under Xvfb, with no screen. Two methods were used:

- screenshots from the app's headless mode (`--replay FILE --cursor K --hover SQUARE --analyse --screenshot PNG`);
- real key presses and mouse clicks, sent with XTEST by `tools/app-checks/session.sh` and `drive.py`.

The records and screenshots are in `M7/`. The owner's own hands-on pass is still to do.

| Item | Check | Result |
|---|---|---|
| 9. Replay steps forward and back through a 57-ply game | real keys on a 57-ply record; unit test over all 58 positions | pass |
| 10. Solver colouring matches `fh analyse` on three positions | three positions compared by hand, one per screenshot | pass |
| 11. Blunder markers match games.jsonl for a harness record | gauntlet game 324: 2 + 2 markers, each confirmed red by `fh analyse` | pass |
| 12. "Play from here" continues with the selected players | real clicks; unit test | pass |

## 9. A 57-ply game

57 plies (2 · 4 hounds · 7 rows + 1, `Rules.MaxPliesFrom`) is the longest game the 8×8 board allows. Every hound
has to reach the bottom row.

Under the Classic rules that cannot happen. Before the last hound moves down, the fox would have to stand on the one
free bottom square, or it would count as escaped. That free square is the one the hound needs.

The check therefore uses a Gilith game, built by `tools/app-checks/long57.py` and saved as `M7/long57-gilith.fhg`:
- each hound marches down its own two-file lane in turn;
- the fox shuffles out of the way;
- after ply 57 the hounds cannot move, so the fox wins.

The app loads the record, which replays every ply with legality checks and verifies the stored result.

With real key presses (status lines in `M7/keys-57-plies.png`):

| Keys | Status line |
|---|---|
| (viewer opens) | ply 57 of 57 |
| Home | ply 0 |
| → once | ply 1 |
| → 27 more times | ply 28 |
| → 29 more times | ply 57 |
| → once more | stays at ply 57 |
| ← 57 times | ply 0 |
| End | ply 57 |

The unit test `The_longest_game_the_8x8_board_allows_57_plies_replays_forward_and_back` plays the same game in a
session. It then steps through all 58 positions to the start and back. At each step it compares the board, the last
ply and the next ply with the record's own replay.

## 10. Solver colouring against `fh analyse`

`fh analyse -p POSITION --solver` prints every legal ply with the solver's value after it, counting the ply. The
colour rule was checked by hand against those printed values:

| Colour | Printed value |
|---|---|
| green | the same distance as the position's value |
| yellow | the same winner, at a longer distance |
| red | the other side wins |
| grey | the position was already lost |

| Position (record, ply) | `fh analyse` | Viewer |
|---|---|---|
| `8/e1/b4,d4,f4,e3/H`, hounds win in 9 (m5-hounds game 453, ply 22) | e3-f2 green (9); b4-a3, d4-c3, f4-g3 yellow (11), b4-c3 yellow (13); e3-d2 red (fox wins in 14) | the same; c3 has two yellow markers, one from each hound; tooltip on c3 lists both (`M7/viewer-colours.png`) |
| `8/c3/c5,e5,e3,g3/F`, fox wins in 9 (m5-fox-openings game 79, ply 19) | c3-b4 green (9); c3-d4 yellow (13); c3-b2, c3-d2 red (hounds win in 12) | the same (`M7/hand-check-fox.png`, top) |
| `8/b4/h8,c5,d4,h4/F`, fox wins in 7 (m5-fox-openings game 118, ply 9) | b4-a5 green (7); b4-c3 yellow (15); b4-a3 red (hounds win in 22) | the same (`M7/hand-check-fox.png`, bottom) |

The rule itself is also unit-tested independently. `MoveGradingTests` grades every position of the 5×5, 6×6 and
7×7 tables against the children's values read straight from the table.

## 11. Blunder markers against games.jsonl

The record is game 324 of `m6-promotion-gauntlet`: lin-s1-n8 against lin-v0 at 1,000 nodes, saved by the harness.
It is in `M7/gauntlet-game-000324.fhg`, with its games.jsonl line beside it. The line says
`"blunders":{"fox":2,"hounds":2}`.

The viewer marks four plies "??" (`M7/blunders-game-324.png`): two fox plies and two hound plies.

`fh analyse --solver` on the position before each marked ply rates the played ply red:

| Marked ply | Position before it | Value before | Value after the ply |
|---|---|---|---|
| 3. f2-g3 | `8/f2/d8,g7,b6,f4/F` | fox wins in 27 | hounds win in 30 |
| 3. ... g7-f6 | `8/g3/d8,g7,b6,f4/H` | hounds win in 29 | fox wins in 26 |
| 4. g3-f2 | `8/g3/d8,f6,b6,f4/F` | fox wins in 25 | hounds win in 28 |
| 4. ... d8-c7 | `8/f2/d8,f6,b6,f4/H` | hounds win in 27 | fox wins in 24 |

An unmarked ply in a lost position, 2. ... a7-b6, is grey.

The harness and the viewer use the same function, `MoveGrading.Judge`. The harness was switched to it in this
milestone. A rerun of `m6-hounds-lin-v0-1000` afterwards gave a byte-identical summary.json and the same games.

## 12. Play from here

With real clicks on the replay of m5-hounds game 453 (`M7/play-from-here.png`):

1. "6. d2-e3" was clicked in the move list, and the viewer jumped to ply 10.
2. The cyclers set Fox to Human and Hounds to Easy.
3. "Play from here" was clicked. A new game started from `8/e3/e7,c5,g5,d4/H`.
4. The Easy hounds played g5-f4, and the game then waited for the human fox.

The unit test `Play_from_here_starts_a_game_from_the_shown_position_with_the_selected_players` checks:
- the new start position;
- that the redo history is empty;
- that the players are kept;
- the selected AI's first ply.

## Analyse

"Analyse" was checked with clicks in two places (`M7/analyse-16x16.png`):

| Where | While it ran | Result |
|---|---|---|
| 16×16 start position, no solver | after 0.6 s the panel showed "analysing - depth 16, 2.1M nodes" and the button read "Stop" | at 2.0 s: i1-h2, +119, depth 17, 6.2 M nodes |
| m5-hounds game 453, ply 10 | finished within half a second | e7-d6, hounds win in 21, depth 21: the solver's value |

The 8×8 result disappeared when the viewer stepped one ply on, and came back when it stepped back.
