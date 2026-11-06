# Fox and Hounds 1.0: user guide

Fox and Hounds is a board game for two sides on the dark squares of a chequered board:
- one fox, moving diagonally in any direction;
- a pack of hounds that can only move forward.

The fox tries to get past the hounds. The hounds try to corner it.

This program has two parts:
- **FoxHounds**: the game, played with the mouse, against the computer or another person. It has an analysis
  viewer that shows, for every possible move, whether it keeps a win or throws it away.
- **fh**: a command-line tool for studying the game. It solves boards exactly, plays thousands of games between
  computer players, grades moves and tunes the computer players' evaluation.

## 1. Installing and starting

Download the archive for your system, unpack it anywhere and run the program from the unpacked folder. Nothing else
needs to be installed: the .NET runtime is inside the folder.

| System | Archive | Start the game | Start the tool |
|---|---|---|---|
| Windows 10 or 11, 64-bit | `FoxHounds-1.0.0-win-x64.zip` | double-click `FoxHounds.exe` | `fh.exe` in a terminal |
| Linux, 64-bit | `FoxHounds-1.0.0-linux-x64.tar.gz` | `./FoxHounds` | `./fh` |
| macOS, Apple silicon | `FoxHounds-1.0.0-osx-arm64.tar.gz` | `./FoxHounds` in Terminal | `./fh` in Terminal |

The programs are not signed by a publisher, so the system may hold them back the first time:

- **Windows:** if "Windows protected your PC" appears, choose *More info*, then *Run anyway*.
- **macOS:** files downloaded from the internet are quarantined. Once, in Terminal, in the folder that holds the
  unpacked folder, run:
  ```
  xattr -dr com.apple.quarantine FoxHounds-1.0.0-osx-arm64
  ```
- **Linux:** the game needs a desktop session and a graphics driver with OpenGL 3.3. `tar` keeps the files
  executable; after unpacking a copy some other way, run `chmod +x FoxHounds fh`.

The game opens a window of 1100 × 760 pixels. You can resize it down to 800 × 600.

## 2. The rules

- **The board** is n × n squares, with n from 4 to 16; the standard game is 8 × 8. Only the dark squares are used.
- **Setup:** the hounds start on every dark square of the top row: ⌊n/2⌋ hounds, so four on 8 × 8. The fox starts
  on the bottom row, on e1 on 8 × 8, or on a bottom-row square it chooses if "Fox start" is set to *fox chooses*.
- **Turns:** the fox moves first. On each turn a side must make one move:
  - the fox moves one square diagonally in any of the four directions, to an empty square;
  - the hounds move *one* hound one square diagonally forward, down the board, to an empty square.
- There are no jumps, captures or passes.
- **How a game ends** (the first rule that applies decides):

  | Rule | Winner | When |
  |---|---|---|
  | Fox reaches home | fox | the fox reaches the top row (Classic rules only) |
  | Fox trapped | hounds | it is the fox's turn and it has no move |
  | Hounds stuck | fox | it is the hounds' turn and no hound can move (both rule sets) |
  | Fox escaped | fox | the fox is level with or above the highest hound, so no hound can ever block it again (Classic rules only) |

- **Two rule sets**, chosen with *Rules* in the panel:
  - **Classic** is the common published game.
  - **Gilith** is the variant from the published computer solution at gilith.com. The fox does not win by reaching
    the top row, only by leaving the hounds without a move, and there is no early end for an escaped fox.

With perfect play the hounds win the Classic 8 × 8 game, in 44 moves counted one side at a time ("plies"). Smaller
and odd-sized boards can go either way. On boards up to 11 × 11 the program knows the exact result of every
position.

## 3. Playing

The board is on the left. The panel on the right has, from top to bottom:
- the game settings;
- the two players;
- a status line, which says whose turn it is, what happened, or why something could not be done;
- an evaluation line;
- the list of moves;
- the buttons.

**Moving:** click one of your pieces. Its possible moves are marked with dots; click one of them. When only one of
your pieces can reach a square, you can click that square directly. When the fox chooses its start, the possible
start squares are marked: click one.

**Settings:** *Board* (4 × 4 to 16 × 16), *Rules* (Classic or Gilith) and *Fox start* (fixed or fox chooses).
Changing any of them starts a new game.

**Players:** each side can be any of these, set with the arrows beside *Fox* and *Hounds*:

| Player | What it is |
|---|---|
| Human | you, or a friend at the same computer |
| Easy | looks 2 plies ahead (one move each); one move in four is random |
| Medium | looks 6 plies ahead; one move in twenty is random |
| Hard | searches up to 2 million positions or 3 seconds per move, and picks at random among equally good moves |
| Perfect | the exact solver: when winning, it wins as fast as possible; when losing, it holds out as long as possible. It picks at random among equally good moves. Offered on boards up to 11 × 11 |
| Custom: *name* | a computer player you described in a file (section 7) |

A new player for a side takes over at that side's next turn. A newly chosen computer player waits half a second
before moving, so you can click through the list without the players you pass making moves. When both sides are
computer players, *Play*/*Pause* (or the space bar) stops and resumes the game, and *Delay* sets the pause between
moves.

**Perfect play needs a table** of every position's value:
- tables up to 8 × 8 are built in moments with each game;
- 9 × 9 to 11 × 11 tables are built the first time a Perfect player is chosen, with progress shown on the
  evaluation line. On a two-core computer: 9 × 9 about a second; 10 × 10 about 10 seconds and 212 MB; 11 × 11 about
  half a minute to a minute and 655 MB.

Tables are kept in the data folder (section 8), so each is built only once.

**The evaluation line** shows the score behind the latest computer move: "+" is good for the fox, and a forced win the
search has found is spelled out, such as "Fox wins in <= 9". When a table is loaded, it also shows the exact result
of the current position, such as "solver: Hounds win in 44".

**Undo and Redo** (Ctrl+Z and Ctrl+Y) go back to your previous turn and forward again. Against the computer, Undo
takes back the computer's reply as well.

**When the game ends** a banner shows the winner and the reason, with *New game*, *Save* and *Replay*. Click the board
or press Esc to hide it.

## 4. Saving and loading

*Save* (Ctrl+S) stores the game in the data folder's `games` folder, named after the time the game started. Saving
the same game again updates the file.

*Load* (Ctrl+O) replaces the move list with your saved games, newest first; click one to open it. A loaded game can
be played on if it had not ended. Esc closes the list.

Saved games (`.fhg` files) are plain JSON. They hold the rules, the players, every move and, for each computer
move, its score, search depth, positions searched and planned line.

## 5. The replay and analysis viewer

Press *Replay* to step through the current game: the one just finished, or one you loaded. While the viewer is open,
the game itself waits.

| Control | Effect |
|---|---|
| ← and → | one move back and forward (hold to repeat) |
| Home, End | the start and the end of the game |
| click a move in the list | the position after that move |
| Esc or *Back to game* | close the viewer |
| Ctrl+C | copy the shown position as text, for `fh analyse` |

**Solver colours.** When the board has a table (up to 11 × 11), every possible move in the shown position gets a
coloured marker on its target square:

| Colour | Meaning |
|---|---|
| green | keeps the win, as fast as possible |
| yellow | keeps the win, but more slowly |
| red | a blunder: it turns a win into a loss |
| grey | the position was already lost, so no move can lose anything |

Hold the mouse over a square to see the exact result after each move into it. The move the game actually went on
with has a dark ring.

**The move list** marks every blunder with a red **??**.

**The panel** shows the move that led to the shown position: its number, its colour class, and what the computer
player knew when it chose it (score, depth, positions searched, time, planned line).

**Analyse** searches the shown position for up to two seconds and shows the best move it finds, its score and its
line; *Stop* ends it early. Each result stays with its position while the viewer is open.

**Play from here** starts a new game from the shown position. It uses the players selected in the panel, which you
can change in the viewer first.

## 6. Starting the game from the command line

| Option | Effect |
|---|---|
| `--load FILE` | open a saved game |
| `--replay FILE` | open a saved game in the viewer |
| `--size 1280x900` | the window size |
| `--data FOLDER` | keep tables, saved games and custom players in FOLDER instead of the usual data folder |

## 7. Custom computer players

Put a player file (JSON) in the data folder's `agents` folder. It appears as "Custom: *name*" in the player lists the
next time the game starts. A file that cannot be read is skipped, and the status line names it. For example:

```json
{
  "format": "agent/1",
  "type": "minimax",
  "name": "deep",
  "evaluator": { "type": "linear" },
  "search": { "ttMegabytes": 64, "rootSelection": "best" },
  "budget": { "maxNodes": 5000000, "hardTimeMs": 5000 },
  "epsilon": 0.0
}
```

| Field | Values |
|---|---|
| `type` | `minimax` (the search), `solver` (perfect play, boards up to 11 × 11), `random` |
| `evaluator` | `linear` or `tapered`. Add `"weightsFile": "my-weights.json"` to use weights from `fh tune`; a relative path is read from the player file's folder |
| `search` | `ttMegabytes` (memory for remembered positions); `rootSelection`, either `best` or `randomAmongBest` |
| `budget` | `maxDepth` (plies ahead), `maxNodes` (positions searched), `softTimeMs` (start no deeper search after this many milliseconds), `hardTimeMs` (stop at this many milliseconds); any combination, and the search stops at the first limit it reaches |
| `epsilon` | the chance of a random move instead of the chosen one, from 0 to 1 |
| `randomTies` | for `solver`: pick at random among equally good moves |

## 8. The data folder

| System | Folder |
|---|---|
| Windows | `%LOCALAPPDATA%\FoxHounds`, for example `C:\Users\you\AppData\Local\FoxHounds` |
| Linux | `~/.local/share/FoxHounds`, or `$XDG_DATA_HOME/FoxHounds` |
| macOS | `~/Library/Application Support/FoxHounds` |

It holds:
- `tables`: solver tables. You can delete them; they are rebuilt when needed. The 10 × 10 and 11 × 11 tables are
  the large ones.
- `games`: saved games.
- `agents`: your custom players.

The game and `fh` share the tables.

## 9. The fh tool

`fh --help` lists the commands, and `fh COMMAND --help` lists a command's options. A position is written as
`size/fox/hounds/side`, for example `8/e1/b8,d8,f8,h8/F` for the 8 × 8 start with the fox to move; the viewer copies
this text with Ctrl+C.

| Command | What it does | Example |
|---|---|---|
| `solve` | solves a board exactly and prints the result from each fox start | `fh solve --n 10` |
| `analyse` | searches a position; with `--solver`, also prints the exact value of every possible move and its viewer colour | `fh analyse -p "8/c3/c5,e5,e3,g3/F" --solver` |
| `perft` | counts move sequences, to check move generation | `fh perft --n 8 --depth 10` |
| `openings` | makes a set of starting positions by random play, optionally labelled and filtered by the solver | `fh openings --plies 6-16 --count 1000 --label --filter HoundsWon -o openings.txt` |
| `suite` | makes training, validation and test positions for tuning and grading | `fh suite --out-dir suite --workers 2` |
| `match` | plays an experiment's games and writes `summary.json` and `games.jsonl` | `fh match experiments/m6/hounds-lin-v0-1000.json --out runs/hounds` |
| `grade` | grades a player's moves on a position set against the solver | `fh grade experiments/m6/grade-lin-v0-1000.json --out runs/grade` |
| `report` | prints a run's summary, or compares two runs | `fh report runs/hounds runs/other` |
| `tune` | tunes evaluation weights, by regression on solver labels or by SPSA | `fh tune experiments/tuning/m6-spsa-lin.json --out runs/tuned` |

The `experiments` folder that comes with the program holds the experiments behind the project's recorded results,
with their players, opening sets and position suites. The `m6` examples above take seconds; the `m5` experiments play
at 2 million positions per move and take far longer. Results are reproducible: the same experiment file gives the
same games, whatever the number of workers. Stopping a `match` or `grade` and running it again into the same folder
resumes it.

## 10. Troubleshooting

- **No window appears (Linux):** check that a desktop session is running and that the graphics driver supports
  OpenGL 3.3.
- **"cannot be opened" or "is damaged" (macOS):** remove the quarantine as in section 1.
- **Perfect is missing from the player list:** the solver covers boards up to 11 × 11.
- **"skipped unreadable player files" on the status line:** a file in `agents` is not valid JSON or is not a player
  description; the name is shown, and the game runs without it.
- **A saved game will not load:** the status line says why. Loading checks every move and the stored result, so a
  hand-edited file with an illegal move is refused rather than half-loaded.
- **The data folder grows:** the 10 × 10 and 11 × 11 tables take 212 MB and 655 MB. Delete them from `tables`; they
  are rebuilt when a Perfect player needs them.
