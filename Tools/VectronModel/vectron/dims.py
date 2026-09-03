"""Reference dimensions of a Siemens Vectron (BR 193 family), in metres.

Sources are the publicly documented main dimensions of the type; anything not
published is a plausible reconstruction chosen to keep the proportions right.
Values are kept in one place so the whole model scales consistently.
"""

# --- overall -------------------------------------------------------------
LENGTH_OVER_BUFFERS = 18.980
BODY_HALF_LEN = 8.950          # front face of the head stock
BUFFER_FACE_X = LENGTH_OVER_BUFFERS / 2.0
WIDTH = 3.012
HALF_WIDTH = WIDTH / 2.0
HEIGHT_ROOF = 4.220            # top of rail to roof crown, pantograph down

# --- running gear (Bo'Bo') ----------------------------------------------
BOGIE_PIVOT_DIST = 9.900
BOGIE_WHEELBASE = 3.000
WHEEL_DIAMETER = 1.250
WHEEL_RADIUS = WHEEL_DIAMETER / 2.0
TYRE_WIDTH = 0.140
TRACK_GAUGE = 1.435
AXLE_X = (BOGIE_PIVOT_DIST / 2.0 - BOGIE_WHEELBASE / 2.0,
          BOGIE_PIVOT_DIST / 2.0 + BOGIE_WHEELBASE / 2.0)   # (3.45, 6.45)

# --- body envelope -------------------------------------------------------
FLOOR_Z = 1.300                # underside of the main frame
SIDE_BOT_Z = 1.520             # where the skirt reaches full width
SIDE_TOP_Z = 3.620             # top of the flat side wall
ROOF_Z = HEIGHT_ROOF

# --- couplers / buffers --------------------------------------------------
COUPLER_HEIGHT = 1.050         # DV/CCL expects the coupler rig at Y = 1.05
BUFFER_SPACING = 1.750
BUFFER_HALF = BUFFER_SPACING / 2.0
BUFFER_PLATE_R = 0.225
HEADSTOCK_X = BODY_HALF_LEN

# --- cab -----------------------------------------------------------------
CAB_BULKHEAD_X = 7.00
DOOR_X = (6.22, 6.94)
DOOR_Z = (1.540, 3.380)
CAB_WINDOW_X = (7.20, 8.10)
CAB_WINDOW_Z = (2.560, 3.360)

# --- machine room --------------------------------------------------------
GRILLE_Z = (2.300, 3.230)
GRILLE_BAYS = ((0.30, 1.42), (1.66, 2.78), (3.02, 4.14), (4.38, 5.50))

# --- roof ----------------------------------------------------------------
PANTO_X = 3.35                 # centre of each pantograph base
PANTO_BASE_LEN = 2.60
HV_BUS_Z = 4.28
